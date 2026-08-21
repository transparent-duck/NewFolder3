using DeepDungeon.Fsd.Dalamud.Runtime;

namespace NewFolder3;

internal sealed class CompositeRunTelemetryObserver : IRunTelemetryObserver
{
    private readonly IRunTelemetryObserver[] _observers;

    internal CompositeRunTelemetryObserver(params IRunTelemetryObserver[] observers) =>
        _observers = observers ?? throw new ArgumentNullException(nameof(observers));

    public void ObserveWaypointTerminal(in RunWaypointTerminalTelemetry observation)
    {
        foreach (IRunTelemetryObserver observer in _observers)
            observer.ObserveWaypointTerminal(observation);
    }

    public void ObserveFloorBoundary(in RunFloorBoundaryTelemetry observation)
    {
        foreach (IRunTelemetryObserver observer in _observers)
            observer.ObserveFloorBoundary(observation);
    }

    public void ObserveFloorTerminal(in RunFloorTerminalTelemetry observation)
    {
        foreach (IRunTelemetryObserver observer in _observers)
            observer.ObserveFloorTerminal(observation);
    }

    public void ObserveFloorState(RunFloorStateTelemetry observation)
    {
        foreach (IRunTelemetryObserver observer in _observers)
            observer.ObserveFloorState(observation);
    }

    public void ObserveRunRecordingClosed(in RunRecordingClosedTelemetry observation)
    {
        foreach (IRunTelemetryObserver observer in _observers)
            observer.ObserveRunRecordingClosed(observation);
    }
}
