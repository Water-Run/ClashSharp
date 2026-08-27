//! Deadline-bound child-process execution with bounded output capture.
//!
//! Running Windows children are assigned to a kill-on-close Job before caller-owned stdin is
//! released. stdout and stderr are drained concurrently even after their retained byte limit is
//! reached, so a noisy child cannot deadlock on a full pipe or grow Installer memory without
//! bound.
//!
//! An elevated helper started through `RunAs` crosses the caller's Job boundary. Callers that use
//! elevation must therefore pair this runner with durable recovery instead of treating Job
//! ownership as proof that the elevated helper was terminated.

use std::fmt;
use std::io::{Read, Write};
use std::process::{Command, ExitStatus, Stdio};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, TryRecvError};
use std::thread;
use std::time::{Duration, Instant};

#[cfg(windows)]
use std::os::windows::io::AsRawHandle;

/// Shared grace period for Job close, direct-child reap, and detached I/O-worker completion.
///
/// Every teardown wait consumes this one budget instead of receiving a fresh timeout, which keeps
/// the caller's total post-deadline latency bounded.
const PROCESS_TEARDOWN_GRACE: Duration = Duration::from_secs(2);

type WorkerReceiver<T> = Receiver<Result<T, String>>;

/// Immutable limits for one child-process execution.
#[derive(Clone, Copy, Debug)]
pub struct ProcessRunOptions {
    /// Maximum wall-clock time from successful spawn until observed exit.
    pub deadline: Duration,

    /// Maximum retained bytes from stdout. Excess bytes are drained and marked truncated.
    pub stdout_limit_bytes: usize,

    /// Maximum retained bytes from stderr. Excess bytes are drained and marked truncated.
    pub stderr_limit_bytes: usize,

    /// Interval used while polling for child completion.
    pub poll_interval: Duration,
}

impl ProcessRunOptions {
    /// Creates limits with a 20 ms completion polling interval.
    #[must_use]
    pub const fn new(
        deadline: Duration,
        stdout_limit_bytes: usize,
        stderr_limit_bytes: usize,
    ) -> Self {
        Self {
            deadline,
            stdout_limit_bytes,
            stderr_limit_bytes,
            poll_interval: Duration::from_millis(20),
        }
    }

    fn validate(self) -> Result<Self, ProcessRunError> {
        if self.deadline.is_zero()
            || self.stdout_limit_bytes == 0
            || self.stderr_limit_bytes == 0
            || self.poll_interval.is_zero()
        {
            return Err(ProcessRunError::new(
                "installer.process.invalid_options",
                "deadline, output limits, and poll interval must be positive",
            ));
        }

        Ok(self)
    }
}

/// Completed child-process result with independently bounded stdout and stderr.
#[derive(Debug)]
pub struct BoundedProcessOutput {
    /// Child exit status.
    pub status: ExitStatus,

    /// Retained stdout prefix, no longer than its configured limit.
    pub stdout: Vec<u8>,

    /// Retained stderr prefix, no longer than its configured limit.
    pub stderr: Vec<u8>,

    /// True when additional stdout bytes were drained but not retained.
    pub stdout_truncated: bool,

    /// True when additional stderr bytes were drained but not retained.
    pub stderr_truncated: bool,
}

/// Stable process-runner failure with a machine-readable code and bounded detail.
#[derive(Debug)]
pub struct ProcessRunError {
    code: &'static str,
    detail: String,
}

impl ProcessRunError {
    fn new(code: &'static str, detail: impl Into<String>) -> Self {
        Self {
            code,
            detail: detail.into(),
        }
    }

    /// Returns the stable diagnostic code for programmatic classification.
    #[must_use]
    pub const fn code(&self) -> &'static str {
        self.code
    }
}

impl fmt::Display for ProcessRunError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.detail.is_empty() {
            formatter.write_str(self.code)
        } else {
            write!(formatter, "{}: {}", self.code, self.detail)
        }
    }
}

impl std::error::Error for ProcessRunError {}

/// Runs one child with a deadline, bounded concurrent output capture, and Windows Job ownership.
///
/// When `stdin_bytes` is present, the child is assigned to its Job before a writer thread releases
/// those bytes. A timeout requests Job/direct-child termination, closes the kill-on-close Job, and
/// then spends one fixed grace budget polling for direct-child reap and I/O-worker completion. A
/// worker that misses that budget is detached and reported through a stable error instead of being
/// joined indefinitely. A no-stdin command may finish between spawn and Job assignment; its
/// already-completed status and bounded output are still collected. Callers must use the stdin gate
/// for mutating scripts that must not start before Job ownership is established.
///
/// # Errors
///
/// Returns a stable [`ProcessRunError`] for invalid limits, Job setup, spawn, pipe, stdin, wait,
/// timeout, termination, output-drain, or capture-thread failures.
pub fn run_bounded_process(
    command: &mut Command,
    stdin_bytes: Option<&[u8]>,
    options: ProcessRunOptions,
) -> Result<BoundedProcessOutput, ProcessRunError> {
    let options = options.validate()?;
    let mut job = KillOnCloseJob::create()?;
    command
        .stdin(if stdin_bytes.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());

    let mut child = command.spawn().map_err(|error| {
        ProcessRunError::new("installer.process.spawn_failed", error.to_string())
    })?;
    let started = Instant::now();
    let completed_before_job_assignment = if let Err(assign_error) = job.assign(&child) {
        // Very short read-only commands (notably `reg.exe` queries) can exit between spawn and
        // AssignProcessToJobObject. Treat only an observed completed child as a successful run so
        // this benign race does not make the query flaky. A child that is still running is killed
        // and reaped; mutating scripts use the stdin gate and therefore cannot reach their payload
        // before successful assignment.
        match child.try_wait() {
            Ok(Some(status)) if stdin_bytes.is_none() => Some(status),
            Ok(Some(_)) => {
                drop(job);
                return Err(assign_error);
            }
            Ok(None) => {
                let teardown_deadline = teardown_deadline();
                let related = terminate_job_and_child(
                    job,
                    &mut child,
                    teardown_deadline,
                    options.poll_interval,
                );
                return Err(with_related_errors(assign_error, related));
            }
            Err(wait_error) => {
                let teardown_deadline = teardown_deadline();
                let mut related = terminate_job_and_child(
                    job,
                    &mut child,
                    teardown_deadline,
                    options.poll_interval,
                );
                related.insert(
                    0,
                    ProcessRunError::new(
                        "installer.process.post_assignment_wait_failed",
                        wait_error.to_string(),
                    ),
                );
                return Err(with_related_errors(assign_error, related));
            }
        }
    } else {
        None
    };

    let stdout = match child.stdout.take() {
        Some(stdout) => stdout,
        None => {
            let teardown_deadline = teardown_deadline();
            let related =
                terminate_job_and_child(job, &mut child, teardown_deadline, options.poll_interval);
            return Err(with_related_errors(
                ProcessRunError::new(
                    "installer.process.stdout_unavailable",
                    "stdout pipe was not created",
                ),
                related,
            ));
        }
    };
    let stderr = match child.stderr.take() {
        Some(stderr) => stderr,
        None => {
            drop(stdout);
            let teardown_deadline = teardown_deadline();
            let related =
                terminate_job_and_child(job, &mut child, teardown_deadline, options.poll_interval);
            return Err(with_related_errors(
                ProcessRunError::new(
                    "installer.process.stderr_unavailable",
                    "stderr pipe was not created",
                ),
                related,
            ));
        }
    };
    let stdout_capture = match spawn_capture(stdout, options.stdout_limit_bytes, "stdout") {
        Ok(capture) => capture,
        Err(error) => {
            drop(stderr);
            let teardown_deadline = teardown_deadline();
            let related =
                terminate_job_and_child(job, &mut child, teardown_deadline, options.poll_interval);
            return Err(with_related_errors(error, related));
        }
    };
    let stderr_capture = match spawn_capture(stderr, options.stderr_limit_bytes, "stderr") {
        Ok(capture) => capture,
        Err(error) => {
            let teardown_deadline = teardown_deadline();
            let mut related =
                terminate_job_and_child(job, &mut child, teardown_deadline, options.poll_interval);
            if let Err(capture_error) = receive_capture(stdout_capture, "stdout", teardown_deadline)
            {
                related.push(capture_error);
            }
            return Err(with_related_errors(error, related));
        }
    };
    let stdin_writer = if let Some(bytes) = stdin_bytes {
        let stdin = match child.stdin.take() {
            Some(stdin) => stdin,
            None => {
                let teardown_deadline = teardown_deadline();
                let mut related = terminate_job_and_child(
                    job,
                    &mut child,
                    teardown_deadline,
                    options.poll_interval,
                );
                collect_capture_cleanup_error(
                    stdout_capture,
                    "stdout",
                    teardown_deadline,
                    &mut related,
                );
                collect_capture_cleanup_error(
                    stderr_capture,
                    "stderr",
                    teardown_deadline,
                    &mut related,
                );
                return Err(with_related_errors(
                    ProcessRunError::new(
                        "installer.process.stdin_unavailable",
                        "stdin pipe was not created",
                    ),
                    related,
                ));
            }
        };
        let input = bytes.to_vec();
        match spawn_stdin_writer(stdin, input) {
            Ok(writer) => Some(writer),
            Err(error) => {
                let teardown_deadline = teardown_deadline();
                let mut related = terminate_job_and_child(
                    job,
                    &mut child,
                    teardown_deadline,
                    options.poll_interval,
                );
                collect_capture_cleanup_error(
                    stdout_capture,
                    "stdout",
                    teardown_deadline,
                    &mut related,
                );
                collect_capture_cleanup_error(
                    stderr_capture,
                    "stderr",
                    teardown_deadline,
                    &mut related,
                );
                return Err(with_related_errors(error, related));
            }
        }
    } else {
        None
    };

    let status = if let Some(status) = completed_before_job_assignment {
        status
    } else {
        loop {
            match child.try_wait() {
                Ok(Some(status)) => break status,
                Ok(None) if started.elapsed() >= options.deadline => {
                    let teardown_deadline = teardown_deadline();
                    let termination_errors = terminate_job_and_child(
                        job,
                        &mut child,
                        teardown_deadline,
                        options.poll_interval,
                    );
                    let stdin_result = receive_stdin_writer(stdin_writer, teardown_deadline);
                    let stdout_result =
                        receive_capture(stdout_capture, "stdout", teardown_deadline);
                    let stderr_result =
                        receive_capture(stderr_capture, "stderr", teardown_deadline);
                    let detail = timeout_detail(
                        options.deadline,
                        &termination_errors,
                        stdin_result.err(),
                        &stdout_result,
                        &stderr_result,
                    );
                    return Err(ProcessRunError::new("installer.process.timed_out", detail));
                }
                Ok(None) => {
                    let remaining = options.deadline.saturating_sub(started.elapsed());
                    thread::sleep(options.poll_interval.min(remaining));
                }
                Err(error) => {
                    let teardown_deadline = teardown_deadline();
                    let mut related = terminate_job_and_child(
                        job,
                        &mut child,
                        teardown_deadline,
                        options.poll_interval,
                    );
                    if let Err(stdin_error) = receive_stdin_writer(stdin_writer, teardown_deadline)
                    {
                        related.push(stdin_error);
                    }
                    collect_capture_cleanup_error(
                        stdout_capture,
                        "stdout",
                        teardown_deadline,
                        &mut related,
                    );
                    collect_capture_cleanup_error(
                        stderr_capture,
                        "stderr",
                        teardown_deadline,
                        &mut related,
                    );
                    return Err(with_related_errors(
                        ProcessRunError::new("installer.process.wait_failed", error.to_string()),
                        related,
                    ));
                }
            }
        }
    };

    // Closing an assigned kill-on-close Job after the direct child exits terminates descendants
    // in that Job and closes inherited pipes before the capture/writer results are received. The
    // already-completed pre-assignment race above leaves an empty Job and is limited to short
    // no-stdin callers in production.
    drop(job);
    let teardown_deadline = teardown_deadline();
    let stdin_result = receive_stdin_writer(stdin_writer, teardown_deadline);
    let stdout_result = receive_capture(stdout_capture, "stdout", teardown_deadline);
    let stderr_result = receive_capture(stderr_capture, "stderr", teardown_deadline);
    match (stdin_result, stdout_result, stderr_result) {
        (Ok(()), Ok(stdout), Ok(stderr)) => Ok(BoundedProcessOutput {
            status,
            stdout: stdout.bytes,
            stderr: stderr.bytes,
            stdout_truncated: stdout.truncated,
            stderr_truncated: stderr.truncated,
        }),
        (stdin, stdout, stderr) => {
            let mut errors = Vec::new();
            if let Err(error) = stdin {
                errors.push(error);
            }
            if let Err(error) = stdout {
                errors.push(error);
            }
            if let Err(error) = stderr {
                errors.push(error);
            }
            let primary = errors.remove(0);
            Err(with_related_errors(primary, errors))
        }
    }
}

#[derive(Debug)]
struct CapturedStream {
    bytes: Vec<u8>,
    truncated: bool,
}

fn spawn_capture<R>(
    reader: R,
    limit: usize,
    stream_name: &'static str,
) -> Result<WorkerReceiver<CapturedStream>, ProcessRunError>
where
    R: Read + Send + 'static,
{
    let (sender, receiver) = mpsc::sync_channel(1);
    thread::Builder::new()
        .name(format!("clashsharp-{stream_name}-capture"))
        .spawn(move || {
            let _ = sender.send(capture_stream(reader, limit));
        })
        .map_err(|error| {
            ProcessRunError::new(
                "installer.process.capture_thread_start_failed",
                format!("{stream_name}: {error}"),
            )
        })?;
    Ok(receiver)
}

fn capture_stream<R>(mut reader: R, limit: usize) -> Result<CapturedStream, String>
where
    R: Read,
{
    let mut retained = Vec::with_capacity(limit.min(64 * 1024));
    let mut truncated = false;
    let mut buffer = [0_u8; 8 * 1024];
    loop {
        let count = reader
            .read(&mut buffer)
            .map_err(|error| error.to_string())?;
        if count == 0 {
            break;
        }

        let remaining = limit.saturating_sub(retained.len());
        let retain_count = remaining.min(count);
        retained.extend_from_slice(&buffer[..retain_count]);
        truncated |= retain_count < count;
    }

    Ok(CapturedStream {
        bytes: retained,
        truncated,
    })
}

fn receive_capture(
    receiver: WorkerReceiver<CapturedStream>,
    stream_name: &'static str,
    deadline: Instant,
) -> Result<CapturedStream, ProcessRunError> {
    match receive_worker_until(receiver, deadline) {
        Ok(Ok(capture)) => Ok(capture),
        Ok(Err(error)) => Err(ProcessRunError::new(
            "installer.process.output_read_failed",
            format!("{stream_name}: {error}"),
        )),
        Err(RecvTimeoutError::Timeout) => Err(ProcessRunError::new(
            "installer.process.output_drain_timed_out",
            stream_name,
        )),
        Err(RecvTimeoutError::Disconnected) => Err(ProcessRunError::new(
            "installer.process.capture_thread_failed",
            stream_name,
        )),
    }
}

fn spawn_stdin_writer<W>(
    mut stdin: W,
    input: Vec<u8>,
) -> Result<WorkerReceiver<()>, ProcessRunError>
where
    W: Write + Send + 'static,
{
    let (sender, receiver) = mpsc::sync_channel(1);
    thread::Builder::new()
        .name(String::from("clashsharp-stdin-writer"))
        .spawn(move || {
            let result = stdin.write_all(&input).map_err(|error| error.to_string());
            drop(stdin);
            let _ = sender.send(result);
        })
        .map_err(|error| {
            ProcessRunError::new(
                "installer.process.stdin_thread_start_failed",
                error.to_string(),
            )
        })?;
    Ok(receiver)
}

fn receive_stdin_writer(
    receiver: Option<WorkerReceiver<()>>,
    deadline: Instant,
) -> Result<(), ProcessRunError> {
    let Some(receiver) = receiver else {
        return Ok(());
    };

    match receive_worker_until(receiver, deadline) {
        Ok(Ok(())) => Ok(()),
        Ok(Err(error)) => Err(ProcessRunError::new(
            "installer.process.stdin_write_failed",
            error,
        )),
        Err(RecvTimeoutError::Timeout) => Err(ProcessRunError::new(
            "installer.process.stdin_write_timed_out",
            "stdin writer missed teardown grace",
        )),
        Err(RecvTimeoutError::Disconnected) => Err(ProcessRunError::new(
            "installer.process.stdin_thread_failed",
            "stdin writer panicked",
        )),
    }
}

fn receive_worker_until<T>(
    receiver: WorkerReceiver<T>,
    deadline: Instant,
) -> Result<Result<T, String>, RecvTimeoutError> {
    let remaining = deadline.saturating_duration_since(Instant::now());
    if remaining.is_zero() {
        return match receiver.try_recv() {
            Ok(result) => Ok(result),
            Err(TryRecvError::Empty) => Err(RecvTimeoutError::Timeout),
            Err(TryRecvError::Disconnected) => Err(RecvTimeoutError::Disconnected),
        };
    }
    receiver.recv_timeout(remaining)
}

fn collect_capture_cleanup_error(
    receiver: WorkerReceiver<CapturedStream>,
    stream_name: &'static str,
    deadline: Instant,
    errors: &mut Vec<ProcessRunError>,
) {
    if let Err(error) = receive_capture(receiver, stream_name, deadline) {
        errors.push(error);
    }
}

fn timeout_detail(
    deadline: Duration,
    termination_errors: &[ProcessRunError],
    stdin_error: Option<ProcessRunError>,
    stdout: &Result<CapturedStream, ProcessRunError>,
    stderr: &Result<CapturedStream, ProcessRunError>,
) -> String {
    let mut details = vec![format!("deadline_ms={}", deadline.as_millis())];
    for error in termination_errors {
        details.push(error.to_string());
    }
    if let Some(error) = stdin_error {
        details.push(error.to_string());
    }
    if let Ok(capture) = stderr
        && !capture.bytes.is_empty()
    {
        details.push(format!(
            "stderr={}{}",
            String::from_utf8_lossy(&capture.bytes).trim(),
            if capture.truncated {
                " [truncated]"
            } else {
                ""
            },
        ));
    } else if let Ok(capture) = stdout
        && !capture.bytes.is_empty()
    {
        details.push(format!(
            "stdout={}{}",
            String::from_utf8_lossy(&capture.bytes).trim(),
            if capture.truncated {
                " [truncated]"
            } else {
                ""
            },
        ));
    }
    if let Err(error) = stdout {
        details.push(error.to_string());
    }
    if let Err(error) = stderr {
        details.push(error.to_string());
    }
    details.join("; ")
}

fn terminate_job_and_child(
    mut job: KillOnCloseJob,
    child: &mut std::process::Child,
    deadline: Instant,
    poll_interval: Duration,
) -> Vec<ProcessRunError> {
    let mut errors = Vec::new();
    if let Err(error) = job.terminate() {
        errors.push(error);
    }
    let child_kill_error = child.kill().err();

    // KILL_ON_JOB_CLOSE is the final non-waiting termination request. Close it before attempting
    // any reap so a failed TerminateJobObject/direct kill cannot strand us behind Child::wait.
    drop(job);
    if let Err(error) = reap_child_until(child, deadline, poll_interval) {
        if let Some(kill_error) = child_kill_error {
            errors.push(ProcessRunError::new(
                "installer.process.child_kill_failed",
                kill_error.to_string(),
            ));
        }
        errors.push(error);
    }
    errors
}

fn reap_child_until(
    child: &mut std::process::Child,
    deadline: Instant,
    poll_interval: Duration,
) -> Result<(), ProcessRunError> {
    loop {
        match child.try_wait() {
            Ok(Some(_)) => return Ok(()),
            Ok(None) => {
                let remaining = deadline.saturating_duration_since(Instant::now());
                if remaining.is_zero() {
                    return Err(ProcessRunError::new(
                        "installer.process.child_reap_timed_out",
                        format!("grace_ms={}", PROCESS_TEARDOWN_GRACE.as_millis()),
                    ));
                }
                thread::sleep(poll_interval.min(remaining));
            }
            Err(error) => {
                return Err(ProcessRunError::new(
                    "installer.process.child_reap_failed",
                    error.to_string(),
                ));
            }
        }
    }
}

fn teardown_deadline() -> Instant {
    Instant::now() + PROCESS_TEARDOWN_GRACE
}

fn with_related_errors(
    mut primary: ProcessRunError,
    related: impl IntoIterator<Item = ProcessRunError>,
) -> ProcessRunError {
    for error in related {
        if !primary.detail.is_empty() {
            primary.detail.push_str("; ");
        }
        primary.detail.push_str(&error.to_string());
    }
    primary
}

#[cfg(windows)]
struct KillOnCloseJob {
    handle: *mut std::ffi::c_void,
}

#[cfg(windows)]
impl KillOnCloseJob {
    fn create() -> Result<Self, ProcessRunError> {
        const JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS: u32 = 9;
        const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: u32 = 0x0000_2000;

        #[repr(C)]
        #[derive(Default)]
        struct BasicLimitInformation {
            per_process_user_time_limit: i64,
            per_job_user_time_limit: i64,
            limit_flags: u32,
            minimum_working_set_size: usize,
            maximum_working_set_size: usize,
            active_process_limit: u32,
            affinity: usize,
            priority_class: u32,
            scheduling_class: u32,
        }

        #[repr(C)]
        #[derive(Default)]
        struct IoCounters {
            read_operation_count: u64,
            write_operation_count: u64,
            other_operation_count: u64,
            read_transfer_count: u64,
            write_transfer_count: u64,
            other_transfer_count: u64,
        }

        #[repr(C)]
        #[derive(Default)]
        struct ExtendedLimitInformation {
            basic_limit_information: BasicLimitInformation,
            io_info: IoCounters,
            process_memory_limit: usize,
            job_memory_limit: usize,
            peak_process_memory_used: usize,
            peak_job_memory_used: usize,
        }

        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CreateJobObjectW(
                job_attributes: *mut std::ffi::c_void,
                name: *const u16,
            ) -> *mut std::ffi::c_void;
            fn SetInformationJobObject(
                job: *mut std::ffi::c_void,
                information_class: u32,
                information: *const std::ffi::c_void,
                information_length: u32,
            ) -> i32;
            fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
        }

        // SAFETY: null attributes/name request a private non-inheritable Job object.
        let handle = unsafe { CreateJobObjectW(std::ptr::null_mut(), std::ptr::null()) };
        if handle.is_null() {
            return Err(ProcessRunError::new(
                "installer.process.job_create_failed",
                std::io::Error::last_os_error().to_string(),
            ));
        }
        let mut limits = ExtendedLimitInformation::default();
        limits.basic_limit_information.limit_flags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        // SAFETY: limits has the documented layout and remains live for this call.
        let configured = unsafe {
            SetInformationJobObject(
                handle,
                JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS,
                std::ptr::from_ref(&limits).cast(),
                std::mem::size_of::<ExtendedLimitInformation>() as u32,
            )
        };
        if configured == 0 {
            let error = std::io::Error::last_os_error();
            // SAFETY: handle is live and not owned by a returned guard.
            let _ = unsafe { CloseHandle(handle) };
            return Err(ProcessRunError::new(
                "installer.process.job_configure_failed",
                error.to_string(),
            ));
        }
        Ok(Self { handle })
    }

    fn assign(&mut self, child: &std::process::Child) -> Result<(), ProcessRunError> {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn AssignProcessToJobObject(
                job: *mut std::ffi::c_void,
                process: *mut std::ffi::c_void,
            ) -> i32;
        }

        // SAFETY: both handles are live; caller-owned stdin is not released until assignment.
        if unsafe { AssignProcessToJobObject(self.handle, child.as_raw_handle()) } == 0 {
            return Err(ProcessRunError::new(
                "installer.process.job_assign_failed",
                std::io::Error::last_os_error().to_string(),
            ));
        }
        Ok(())
    }

    fn terminate(&mut self) -> Result<(), ProcessRunError> {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn TerminateJobObject(job: *mut std::ffi::c_void, exit_code: u32) -> i32;
        }

        // SAFETY: the guard owns a live Job handle until Drop.
        if unsafe { TerminateJobObject(self.handle, 1) } == 0 {
            return Err(ProcessRunError::new(
                "installer.process.job_terminate_failed",
                std::io::Error::last_os_error().to_string(),
            ));
        }
        Ok(())
    }
}

#[cfg(windows)]
impl Drop for KillOnCloseJob {
    fn drop(&mut self) {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
        }

        // SAFETY: this guard owns the only local Job handle. KILL_ON_JOB_CLOSE intentionally
        // terminates any still-running descendants during Installer teardown or crash.
        let _ = unsafe { CloseHandle(self.handle) };
    }
}

#[cfg(not(windows))]
struct KillOnCloseJob;

#[cfg(not(windows))]
impl KillOnCloseJob {
    fn create() -> Result<Self, ProcessRunError> {
        Ok(Self)
    }

    fn assign(&mut self, _child: &std::process::Child) -> Result<(), ProcessRunError> {
        Ok(())
    }

    fn terminate(&mut self) -> Result<(), ProcessRunError> {
        Ok(())
    }
}

#[cfg(not(windows))]
impl Drop for KillOnCloseJob {
    fn drop(&mut self) {}
}

#[cfg(test)]
mod tests {
    use super::*;

    const CHILD_MODE_ENVIRONMENT_VARIABLE: &str = "CLASHSHARP_PROCESS_RUNNER_CHILD_MODE";
    #[cfg(windows)]
    const CHILD_PID_PATH_ENVIRONMENT_VARIABLE: &str = "CLASHSHARP_PROCESS_RUNNER_CHILD_PID_PATH";

    fn child_command(mode: &str) -> Command {
        let mut command = Command::new(std::env::current_exe().unwrap());
        command
            .args([
                "--exact",
                "process_runner::tests::process_runner_child_entry",
                "--nocapture",
            ])
            .env(CHILD_MODE_ENVIRONMENT_VARIABLE, mode);
        command
    }

    fn test_options(deadline: Duration, limit: usize) -> ProcessRunOptions {
        ProcessRunOptions {
            poll_interval: Duration::from_millis(5),
            ..ProcessRunOptions::new(deadline, limit, limit)
        }
    }

    #[test]
    fn process_runner_child_entry() {
        match std::env::var(CHILD_MODE_ENVIRONMENT_VARIABLE).as_deref() {
            Ok("capture") => {
                std::io::stdout().write_all(b"child-stdout").unwrap();
                std::io::stderr().write_all(b"child-stderr").unwrap();
            }
            Ok("noisy") => {
                std::io::stdout().write_all(&vec![b'o'; 32 * 1024]).unwrap();
                std::io::stderr().write_all(&vec![b'e'; 32 * 1024]).unwrap();
            }
            Ok("stdin") => {
                let mut input = Vec::new();
                std::io::stdin().read_to_end(&mut input).unwrap();
                std::io::stdout().write_all(&input).unwrap();
            }
            Ok("hang") => thread::sleep(Duration::from_secs(30)),
            #[cfg(windows)]
            Ok("tree") => {
                let mut input = Vec::new();
                std::io::stdin().read_to_end(&mut input).unwrap();
                assert_eq!(input, b"release-after-job-assignment");
                let mut grandchild = child_command("hang").spawn().unwrap();
                let pid_path = std::env::var(CHILD_PID_PATH_ENVIRONMENT_VARIABLE).unwrap();
                std::fs::write(pid_path, grandchild.id().to_string()).unwrap();
                thread::sleep(Duration::from_secs(30));
                let _ = grandchild.wait();
            }
            _ => {}
        }
    }

    #[test]
    fn captures_stdout_and_stderr_concurrently() {
        let mut command = child_command("capture");

        let output = run_bounded_process(
            &mut command,
            None,
            test_options(Duration::from_secs(5), 4096),
        )
        .unwrap();

        assert!(output.status.success());
        assert!(String::from_utf8_lossy(&output.stdout).contains("child-stdout"));
        assert!(String::from_utf8_lossy(&output.stderr).contains("child-stderr"));
        assert!(!output.stdout_truncated);
        assert!(!output.stderr_truncated);
    }

    #[test]
    fn drains_but_does_not_retain_output_beyond_each_limit() {
        let mut command = child_command("noisy");

        let output = run_bounded_process(
            &mut command,
            None,
            test_options(Duration::from_secs(5), 1024),
        )
        .unwrap();

        assert!(output.status.success());
        assert_eq!(output.stdout.len(), 1024);
        assert_eq!(output.stderr.len(), 1024);
        assert!(output.stdout_truncated);
        assert!(output.stderr_truncated);
    }

    #[test]
    fn writes_stdin_after_job_assignment_and_closes_the_pipe() {
        let mut command = child_command("stdin");

        let output = run_bounded_process(
            &mut command,
            Some(b"installer-script"),
            test_options(Duration::from_secs(5), 4096),
        )
        .unwrap();

        assert!(output.status.success());
        assert!(String::from_utf8_lossy(&output.stdout).contains("installer-script"));
        assert!(!output.stdout_truncated);
    }

    #[test]
    fn deadline_terminates_and_reaps_a_hung_child() {
        let mut command = child_command("hang");
        let started = Instant::now();
        let child_deadline = Duration::from_millis(150);

        let error = run_bounded_process(&mut command, None, test_options(child_deadline, 1024))
            .unwrap_err();

        assert_eq!(error.code(), "installer.process.timed_out");
        let upper_bound = child_deadline + PROCESS_TEARDOWN_GRACE + Duration::from_secs(3);
        assert!(
            started.elapsed() < upper_bound,
            "deadline teardown exceeded {upper_bound:?}: {error}",
        );
    }

    #[test]
    fn blocked_capture_worker_is_detached_at_the_shared_grace_deadline() {
        struct BlockingReader {
            release: Receiver<()>,
        }

        impl Read for BlockingReader {
            fn read(&mut self, _buffer: &mut [u8]) -> std::io::Result<usize> {
                let _ = self.release.recv();
                Ok(0)
            }
        }

        let (release_sender, release_receiver) = mpsc::channel();
        let capture = spawn_capture(
            BlockingReader {
                release: release_receiver,
            },
            1024,
            "blocked-test",
        )
        .unwrap();
        let started = Instant::now();
        let error = receive_capture(capture, "blocked-test", started + Duration::from_millis(75))
            .unwrap_err();

        assert_eq!(error.code(), "installer.process.output_drain_timed_out");
        assert!(started.elapsed() < Duration::from_secs(1));
        drop(release_sender);
    }

    #[test]
    fn production_teardown_uses_only_bounded_process_and_worker_waits() {
        let source = include_str!("process_runner.rs");
        let production = source.split_once("#[cfg(test)]").unwrap().0;

        assert!(!production.contains(".wait()"));
        assert!(!production.contains(".join()"));
        assert!(!production.contains("wait_with_output"));
        assert!(production.contains("child.try_wait()"));
        assert!(production.contains("receiver.recv_timeout(remaining)"));
        assert!(production.contains("drop(job)"));
    }

    #[test]
    fn rejects_zero_deadline_or_output_limit() {
        let mut command = child_command("capture");

        let error = run_bounded_process(
            &mut command,
            None,
            ProcessRunOptions::new(Duration::ZERO, 0, 0),
        )
        .unwrap_err();

        assert_eq!(error.code(), "installer.process.invalid_options");
    }

    #[cfg(windows)]
    #[test]
    fn windows_deadline_terminates_the_complete_descendant_job_tree() {
        let pid_path = std::env::temp_dir().join(format!(
            "clashsharp-process-runner-child-{}-{}.pid",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos(),
        ));
        let mut command = child_command("tree");
        command.env(CHILD_PID_PATH_ENVIRONMENT_VARIABLE, &pid_path);

        let error = run_bounded_process(
            &mut command,
            Some(b"release-after-job-assignment"),
            // Allow for cold test-host startup and endpoint scanning on Windows runners. The test
            // remains deadline-bound while avoiding a two-second PID-publication race.
            test_options(Duration::from_secs(8), 4096),
        )
        .unwrap_err();

        assert_eq!(error.code(), "installer.process.timed_out");
        let grandchild_pid = read_process_id_with_retry(&pid_path, Duration::from_secs(3));
        let _ = std::fs::remove_file(pid_path);
        assert!(wait_for_process_exit(grandchild_pid, 2_000));
    }

    #[cfg(windows)]
    fn read_process_id_with_retry(path: &std::path::Path, timeout: Duration) -> u32 {
        let started = Instant::now();
        loop {
            if let Ok(contents) = std::fs::read_to_string(path)
                && let Ok(process_id) = contents.parse::<u32>()
            {
                return process_id;
            }
            assert!(
                started.elapsed() < timeout,
                "descendant PID file was not published: {}",
                path.display(),
            );
            thread::sleep(Duration::from_millis(50));
        }
    }

    #[cfg(windows)]
    fn wait_for_process_exit(process_id: u32, timeout_milliseconds: u32) -> bool {
        const SYNCHRONIZE: u32 = 0x0010_0000;
        const WAIT_OBJECT_0: u32 = 0;

        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn OpenProcess(
                desired_access: u32,
                inherit_handle: i32,
                process_id: u32,
            ) -> *mut std::ffi::c_void;
            fn WaitForSingleObject(handle: *mut std::ffi::c_void, milliseconds: u32) -> u32;
            fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
        }

        // SAFETY: the PID comes from the spawned child and the returned handle is closed below.
        let handle = unsafe { OpenProcess(SYNCHRONIZE, 0, process_id) };
        if handle.is_null() {
            return true;
        }
        // SAFETY: handle is live and owned locally for the duration of the wait.
        let wait_result = unsafe { WaitForSingleObject(handle, timeout_milliseconds) };
        // SAFETY: handle has not been closed and is no longer used after this call.
        let _ = unsafe { CloseHandle(handle) };
        wait_result == WAIT_OBJECT_0
    }
}
