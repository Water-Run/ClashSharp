fn main() {
    slint_build::compile("ui/main.slint").unwrap();

    #[cfg(windows)]
    {
        let mut resource = winresource::WindowsResource::new();
        resource.set_icon("LogoInstaller.ico");
        resource.compile().unwrap();
    }
}
