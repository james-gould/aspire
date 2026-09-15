// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// The comparison used to test an observed signal value against a threshold.
/// </summary>
/// <remarks>
/// Values match the <c>SignalOperator</c> enum of the <c>2026-05-01-preview</c> Azure API version. The
/// <c>Dynamic</c> operator is omitted because it relies on Azure-side anomaly detection that has no local equivalent.
/// </remarks>
public enum SignalOperator
{
    /// <summary>The signal breaches when the observed value is greater than the threshold.</summary>
    GreaterThan,

    /// <summary>The signal breaches when the observed value is less than the threshold.</summary>
    LessThan,

    /// <summary>The signal breaches when the observed value is less than or equal to the threshold.</summary>
    LessThanOrEqual,

    /// <summary>The signal breaches when the observed value is greater than or equal to the threshold.</summary>
    GreaterThanOrEqual,

    /// <summary>The signal breaches when the observed value equals the threshold.</summary>
    Equal,

    /// <summary>The signal breaches when the observed value does not equal the threshold.</summary>
    NotEqual
}

/// <summary>
/// A single comparison that moves a signal into a non-healthy state when it matches.
/// </summary>
/// <param name="Operator">The comparison to apply.</param>
/// <param name="Threshold">The value the observed value is compared against.</param>
public sealed record ThresholdRule(SignalOperator Operator, double Threshold)
{
    /// <summary>Determines whether <paramref name="value"/> breaches this rule.</summary>
    public bool IsBreached(double value) => Operator switch
    {
        SignalOperator.GreaterThan => value > Threshold,
        SignalOperator.LessThan => value < Threshold,
        SignalOperator.LessThanOrEqual => value <= Threshold,
        SignalOperator.GreaterThanOrEqual => value >= Threshold,
        SignalOperator.Equal => value == Threshold,
        SignalOperator.NotEqual => value != Threshold,
        _ => false
    };
}

/// <summary>
/// The thresholds that turn an observed signal value into a <see cref="HealthState"/>.
/// </summary>
/// <remarks>
/// Mirrors <c>EvaluationRule</c> in Azure Monitor health models, where the unhealthy rule is required and the
/// degraded rule is optional. When only <see cref="UnhealthyRule"/> is set the signal moves straight from
/// healthy to unhealthy with no intermediate degraded state.
/// </remarks>
/// <param name="UnhealthyRule">The rule that moves the signal to <see cref="HealthState.Unhealthy"/>.</param>
/// <param name="DegradedRule">The optional rule that moves the signal to <see cref="HealthState.Degraded"/>.</param>
public sealed record EvaluationRule(ThresholdRule UnhealthyRule, ThresholdRule? DegradedRule = null)
{
    /// <summary>Evaluates <paramref name="value"/> against the rules and returns the resulting state.</summary>
    public HealthState Evaluate(double value)
    {
        // The unhealthy rule is checked first because both rules can match at once. For example a rule pair
        // of "degraded below 100%, unhealthy below 99%" both match at 98% and unhealthy must win.
        if (UnhealthyRule.IsBreached(value))
        {
            return HealthState.Unhealthy;
        }

        if (DegradedRule?.IsBreached(value) is true)
        {
            return HealthState.Degraded;
        }

        return HealthState.Healthy;
    }
}

/// <summary>
/// A single health indicator attached to an entity.
/// </summary>
/// <remarks>
/// An entity's own state is the worst state across all of its signals, which is then combined with the
/// rolled up state of its dependencies.
/// </remarks>
public sealed record HealthModelSignal
{
    /// <summary>The name of the signal. Must be unique within its entity.</summary>
    public required string Name { get; init; }

    /// <summary>The name shown in the UI. Falls back to <see cref="Name"/> when not set.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The data source the signal reads from.</summary>
    public SignalKind Kind { get; init; } = SignalKind.External;

    /// <summary>
    /// The thresholds applied to <see cref="ObservedValue"/>. When this is <see langword="null"/> the signal
    /// is state-reported and <see cref="ReportedState"/> is used directly.
    /// </summary>
    public EvaluationRule? EvaluationRules { get; init; }

    /// <summary>The most recent numeric value observed for this signal, if it produces one.</summary>
    public double? ObservedValue { get; init; }

    /// <summary>The unit of <see cref="ObservedValue"/>, such as <c>Percent</c> or <c>Count</c>.</summary>
    public string? DataUnit { get; init; }

    /// <summary>
    /// The state reported directly by the producer. Used when <see cref="EvaluationRules"/> is
    /// <see langword="null"/>, which is the case for signals projected from app host health reports.
    /// </summary>
    public HealthState ReportedState { get; init; } = HealthState.Unknown;

    /// <summary>Human readable detail about why the signal is in its current state.</summary>
    public string? Description { get; init; }

    /// <summary>The state this signal contributes to its entity.</summary>
    public HealthState State => EvaluationRules is { } rules && ObservedValue is { } value
        ? rules.Evaluate(value)
        : ReportedState;
}
