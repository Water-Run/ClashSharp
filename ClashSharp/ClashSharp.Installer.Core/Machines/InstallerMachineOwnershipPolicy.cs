using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Machines;

/// <summary>Classifies the fixed machine association observed before mutation.</summary>
public enum InstallerMachineAssociationStatus
{
    /// <summary>No association file exists.</summary>
    Missing,

    /// <summary>An exact strict association exists.</summary>
    Valid,

    /// <summary>Association bytes or filesystem identity are unsafe or invalid.</summary>
    Invalid,
}

/// <summary>Immutable association observation from the protected ProgramData location.</summary>
/// <param name="Status">Observed association status.</param>
/// <param name="Association">Exact association only when <paramref name="Status"/> is valid.</param>
public sealed record InstallerMachineAssociationObservation(
    InstallerMachineAssociationStatus Status,
    InstallerMachineAssociation? Association)
{
    /// <summary>Creates a missing observation.</summary>
    public static InstallerMachineAssociationObservation Missing() =>
        new(InstallerMachineAssociationStatus.Missing, null);

    /// <summary>Creates an invalid observation.</summary>
    public static InstallerMachineAssociationObservation Invalid() =>
        new(InstallerMachineAssociationStatus.Invalid, null);

    /// <summary>Creates a validated observation.</summary>
    public static InstallerMachineAssociationObservation Valid(
        InstallerMachineAssociation association)
    {
        ArgumentNullException.ThrowIfNull(association);
        association.Validate();
        return new(InstallerMachineAssociationStatus.Valid, association);
    }

    /// <summary>Validates status/value consistency.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Status)
            || Status == InstallerMachineAssociationStatus.Valid && Association is null
            || Status != InstallerMachineAssociationStatus.Valid && Association is not null)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_observation_invalid");
        }

        Association?.Validate();
    }
}

/// <summary>Describes whether provisioning can proceed and which credential it must retain.</summary>
public enum InstallerMachineProvisionDisposition
{
    /// <summary>Provision or repair using the returned authentication token.</summary>
    Provision,

    /// <summary>Ordinary install cannot take over ambiguous or foreign machine ownership.</summary>
    RequiresExplicitRepair,
}

/// <summary>Pure ownership decision made before any elevated machine mutation.</summary>
/// <param name="Disposition">Provision or require an explicit reassociation repair.</param>
/// <param name="AuthenticationToken">Existing or fresh credential for provisioning.</param>
public sealed record InstallerMachineProvisionDecision(
    InstallerMachineProvisionDisposition Disposition,
    string? AuthenticationToken)
{
    /// <summary>Validates disposition and token consistency.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Disposition)
            || Disposition == InstallerMachineProvisionDisposition.Provision
                && AuthenticationToken is null
            || Disposition == InstallerMachineProvisionDisposition.RequiresExplicitRepair
                && AuthenticationToken is not null)
        {
            throw new InstallerProtocolException(
                "installer.machine.provision_decision_invalid");
        }

        if (AuthenticationToken is not null)
        {
            InstallerProtocolValidation.ValidateLowerHex256(
                AuthenticationToken,
                "installer.machine.authentication_token_invalid");
        }
    }
}

/// <summary>Pure fail-closed machine ownership policy shared by parent and elevated helper.</summary>
public static class InstallerMachineOwnershipPolicy
{
    /// <summary>Resolves install/repair ownership without changing state.</summary>
    public static InstallerMachineProvisionDecision DecideProvision(
        InstallerRequest request,
        InstallerMachineAssociationObservation observation,
        bool serviceExists,
        bool machineResidueExists,
        string freshAuthenticationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        request.Validate();
        observation.Validate();
        InstallerProtocolValidation.ValidateLowerHex256(
            freshAuthenticationToken,
            "installer.machine.authentication_token_invalid");
        if (request.Operation == InstallerOperation.Uninstall)
        {
            throw new InstallerProtocolException(
                "installer.machine.provision_operation_invalid");
        }

        InstallerMachineProvisionDecision decision;
        if (observation.Association is { } association
            && string.Equals(
                association.OwnerSid,
                request.TargetSid,
                StringComparison.Ordinal))
        {
            decision = new(
                InstallerMachineProvisionDisposition.Provision,
                association.AuthenticationToken);
        }
        else if (!request.AllowReassociation
            && (observation.Status != InstallerMachineAssociationStatus.Missing
                || serviceExists
                || machineResidueExists))
        {
            decision = new(
                InstallerMachineProvisionDisposition.RequiresExplicitRepair,
                null);
        }
        else
        {
            decision = new(
                InstallerMachineProvisionDisposition.Provision,
                freshAuthenticationToken);
        }

        decision.Validate();
        return decision;
    }

    /// <summary>
    /// Returns whether ordinary owner-checked uninstall may remove existing machine resources.
    /// </summary>
    public static bool MayRemove(
        string targetSid,
        InstallerMachineAssociationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        observation.Validate();
        return observation.Association is { } association
            && string.Equals(association.OwnerSid, targetSid, StringComparison.Ordinal);
    }
}
