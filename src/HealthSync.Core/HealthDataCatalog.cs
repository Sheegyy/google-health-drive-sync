using System.Globalization;

namespace HealthSync.Core;

public enum HealthRecordKind { Interval, Sample, Daily, Session, Food, Ecg, Aggregate }

public sealed record HealthDataType(string Id, HealthRecordKind Kind, string Scope, string Operation = "list")
{
    public string Field => Id.Replace('-', '_');

    public string? Filter(QueryWindow window, string timeZone)
    {
        if (Kind is HealthRecordKind.Food or HealthRecordKind.Aggregate) return null;
        var field = Kind switch
        {
            HealthRecordKind.Daily => $"{Field}.date",
            HealthRecordKind.Sample => $"{Field}.sample_time.physical_time",
            HealthRecordKind.Session when Id == "sleep" => "sleep.interval.end_time",
            HealthRecordKind.Session => $"{Field}.interval.civil_start_time",
            _ => $"{Field}.interval.start_time"
        };
        if (Kind == HealthRecordKind.Ecg) return $"{field} >= \"{window.StartUtc}\"";
        var start = Kind == HealthRecordKind.Daily ? window.FirstDate : window.StartUtc;
        var end = Kind == HealthRecordKind.Daily ? window.EndDateExclusive : window.EndUtc;
        if (Kind == HealthRecordKind.Session && Id != "sleep")
        {
            start = DateTimeOffset.Parse(window.StartUtc, CultureInfo.InvariantCulture).AddHours(-14)
                .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            end = DateTimeOffset.Parse(window.EndUtc, CultureInfo.InvariantCulture).AddHours(14)
                .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return $"{field} >= \"{start}\" AND {field} < \"{end}\"";
    }
}

public static class HealthDataCatalog
{
    public const string ScopePrefix = "https://www.googleapis.com/auth/googlehealth.";
    public static readonly HealthDataType[] Types = [
        new("active-energy-burned", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("active-minutes", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("active-zone-minutes", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("altitude", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("distance", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("sedentary-period", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("steps", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("swim-lengths-data", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("time-in-heart-rate-zone", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("activity-level", HealthRecordKind.Interval, "activity_and_fitness"),
        new("floors", HealthRecordKind.Interval, "activity_and_fitness", "rollUp"),
        new("calories-in-heart-rate-zone", HealthRecordKind.Aggregate, "activity_and_fitness", "dailyRollUp"),
        new("total-calories", HealthRecordKind.Aggregate, "activity_and_fitness", "dailyRollUp"),
        new("run-vo2-max", HealthRecordKind.Sample, "activity_and_fitness", "rollUp"),
        new("vo2-max", HealthRecordKind.Sample, "activity_and_fitness"),
        new("daily-vo2-max", HealthRecordKind.Daily, "activity_and_fitness"),
        new("exercise", HealthRecordKind.Session, "activity_and_fitness"),
        new("blood-glucose", HealthRecordKind.Sample, "health_metrics_and_measurements", "rollUp"),
        new("body-fat", HealthRecordKind.Sample, "health_metrics_and_measurements", "rollUp"),
        new("core-body-temperature", HealthRecordKind.Sample, "health_metrics_and_measurements", "rollUp"),
        new("weight", HealthRecordKind.Sample, "health_metrics_and_measurements", "rollUp"),
        new("heart-rate", HealthRecordKind.Sample, "health_metrics_and_measurements", "rollUp"),
        new("heart-rate-variability", HealthRecordKind.Sample, "health_metrics_and_measurements"),
        new("oxygen-saturation", HealthRecordKind.Sample, "health_metrics_and_measurements"),
        new("respiratory-rate-sleep-summary", HealthRecordKind.Sample, "health_metrics_and_measurements"),
        new("height", HealthRecordKind.Sample, "health_metrics_and_measurements"),
        new("daily-heart-rate-variability", HealthRecordKind.Daily, "health_metrics_and_measurements"),
        new("daily-heart-rate-zones", HealthRecordKind.Daily, "health_metrics_and_measurements"),
        new("daily-oxygen-saturation", HealthRecordKind.Daily, "health_metrics_and_measurements"),
        new("daily-respiratory-rate", HealthRecordKind.Daily, "health_metrics_and_measurements"),
        new("daily-resting-heart-rate", HealthRecordKind.Daily, "health_metrics_and_measurements"),
        new("daily-sleep-temperature-derivations", HealthRecordKind.Daily, "health_metrics_and_measurements"),
        new("hydration-log", HealthRecordKind.Session, "nutrition"),
        new("nutrition-log", HealthRecordKind.Session, "nutrition"),
        new("food", HealthRecordKind.Food, "nutrition", "get"),
        new("food-measurement-unit", HealthRecordKind.Food, "nutrition"),
        new("sleep", HealthRecordKind.Session, "sleep", "reconcile"),
        new("electrocardiogram", HealthRecordKind.Ecg, "ecg"),
        new("irregular-rhythm-notification", HealthRecordKind.Session, "irn")
    ];

    public static readonly string[] ReadScopes = [
        .. Types.Select(type => type.Scope).Distinct().Select(scope => $"{ScopePrefix}{scope}.readonly"),
        $"{ScopePrefix}profile.readonly", $"{ScopePrefix}settings.readonly", $"{ScopePrefix}location.readonly"
    ];
}