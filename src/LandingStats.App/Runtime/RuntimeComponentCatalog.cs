using System;
using System.Collections.Generic;

namespace LandingStats.App.Runtime;

internal enum RuntimeComponent
{
    Simulator, Controllers, IngressJournal, Episodes, Facilities, Analysis,
    HistoryArchiveOutbox, TelemetryUpload, CloudSync, Presentation, IntegrationApi
}
internal sealed class RuntimeComponentDefinition
{
    public RuntimeComponentDefinition(RuntimeComponent component, ComponentRecoveryOptions options)
    { Component = component; Options = options; }
    public RuntimeComponent Component { get; }
    public ComponentRecoveryOptions Options { get; }
}
internal static class RuntimeComponentCatalog
{
    // Only policies consumed by actual composition roots belong here. Launcher
    // supervision and helpers within an owner have no independent runtime loop.
    public static IReadOnlyList<RuntimeComponentDefinition> Create() => new[]
    {
        Define(RuntimeComponent.Simulator, 30), Define(RuntimeComponent.Controllers, 10),
        Define(RuntimeComponent.IngressJournal, 30), Define(RuntimeComponent.Episodes, 30),
        Define(RuntimeComponent.Facilities, 90), Define(RuntimeComponent.Analysis, 180),
        Define(RuntimeComponent.HistoryArchiveOutbox, 60),
        // The transport owns its three-minute attempt deadline. Allow the same
        // storage budget as the outbox owner to retire and commit its outcome.
        Define(RuntimeComponent.TelemetryUpload, checked((int)UploadRuntimeOwner.AttemptTimeout.TotalSeconds + 60)),
        Define(RuntimeComponent.CloudSync, 180), Define(RuntimeComponent.Presentation, 30), Define(RuntimeComponent.IntegrationApi, 30)
    };
    private static RuntimeComponentDefinition Define(RuntimeComponent component, int operationSeconds) =>
        new RuntimeComponentDefinition(component, new ComponentRecoveryOptions { OperationDeadline = TimeSpan.FromSeconds(operationSeconds) });
}
