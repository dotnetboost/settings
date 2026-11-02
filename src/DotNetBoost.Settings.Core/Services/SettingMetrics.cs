using System.Diagnostics.Metrics;

namespace DotNetBoost.Settings.Core.Services;

/// <summary>
/// The library's instruments, on the <c>DotNetBoost.Settings</c> meter. Registered by
/// <c>AddSettings()</c> as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// The meter comes from <see cref="IMeterFactory"/> when the container has one, which every
/// generic host does: a meter created that way belongs to the container, so a test host's
/// measurements stay apart from another's and the meter is disposed with it. Without a
/// factory, a plain <c>ServiceCollection</c> or a manager constructed by hand, the instruments
/// fall back to one meter shared by the process.
/// </para>
/// <para>
/// Each failure counter sits beside the log line for the same failure: a swallowed exception
/// is still logged, and the counter is what makes a rate of them visible without anyone
/// reading logs. Every tag is bounded by the application's own types — the implementation,
/// the group, the exception type — never by a value.
/// </para>
/// </remarks>
internal sealed class SettingMetrics
{
    public const string MeterName = "DotNetBoost.Settings";

    public const string WriteObserverFailuresName = "dotnetboost.settings.write_observer.failures";
    public const string ChangeHandlerFailuresName = "dotnetboost.settings.change_handler.failures";

    public const string GroupTag    = "dotnetboost.settings.group";
    public const string ObserverTag = "dotnetboost.settings.observer";
    public const string HandlerTag  = "dotnetboost.settings.handler";
    public const string ErrorTag    = "error.type";

    /// <summary>The instruments on the process-wide meter, for a manager with no container meter.</summary>
    public static SettingMetrics Shared { get; } = new(new Meter(MeterName));

    private readonly Counter<long> _writeObserverFailures;
    private readonly Counter<long> _changeHandlerFailures;

    /// <summary>Creates the instruments on a meter from <paramref name="meterFactory"/>, or on the shared one.</summary>
    public SettingMetrics(IMeterFactory? meterFactory = null)
        : this(meterFactory?.Create(MeterName))
    {
    }

    private SettingMetrics(Meter? meter)
    {
        // Instruments are created once per meter, so the fallback reuses Shared's rather than
        // adding a second set of the same names to the process-wide meter.
        if (meter is null)
        {
            _writeObserverFailures = Shared._writeObserverFailures;
            _changeHandlerFailures = Shared._changeHandlerFailures;
            return;
        }

        _writeObserverFailures = meter.CreateCounter<long>(
            WriteObserverFailuresName, unit: "{failure}",
            description: "Write observers that threw after a settings write committed. The write itself succeeded.");

        _changeHandlerFailures = meter.CreateCounter<long>(
            ChangeHandlerFailuresName, unit: "{failure}",
            description: "Change handlers that threw after a settings write committed. The write itself succeeded.");
    }

    public void WriteObserverFailed(Type observer, string group, Exception ex)
        => _writeObserverFailures.Add(1,
            new(ObserverTag, observer.Name),
            new(GroupTag,    group),
            new(ErrorTag,    ex.GetType().FullName));

    public void ChangeHandlerFailed(Type handler, string group, Exception ex)
        => _changeHandlerFailures.Add(1,
            new(HandlerTag, handler.Name),
            new(GroupTag,   group),
            new(ErrorTag,   ex.GetType().FullName));
}
