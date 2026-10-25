using HealthSync.Core;

internal static class CatalogChecks
{
    public static Task RunAsync()
    {
        Check(HealthDataCatalog.Types.Length == 39, "Expected 39 documented readable v4 data types");
        Check(HealthDataCatalog.Types.Select(type => type.Id).Distinct().Count() == 39, "Duplicate data type IDs");
        Check(HealthDataCatalog.ReadScopes.Length == 9, "Expected nine Health read scopes");
        Check(HealthDataCatalog.ReadScopes.All(scope => scope.StartsWith(HealthDataCatalog.ScopePrefix, StringComparison.Ordinal) &&
            scope.EndsWith(".readonly", StringComparison.Ordinal)), "Only Health read scopes are allowed");
        Check(HealthDataCatalog.Types.All(type => type.Operation is "get" or "list" or "reconcile" or "dailyRollUp" or "rollUp"), "Unexpected write operation");
        var window = QueryWindow.Create(DateTimeOffset.Parse("2026-10-25T03:00:00Z"), new("client", "secret", "refresh", "file"));
        string Filter(string id) => HealthDataCatalog.Types.Single(type => type.Id == id).Filter(window, "Asia/Tokyo")!;
        Check(Filter("heart-rate").StartsWith("heart_rate.sample_time.physical_time >=", StringComparison.Ordinal), "Sample filter mismatch");
        Check(Filter("steps").StartsWith("steps.interval.start_time >=", StringComparison.Ordinal), "Interval filter mismatch");
        Check(Filter("daily-vo2-max").StartsWith("daily_vo2_max.date >=", StringComparison.Ordinal), "Daily filter mismatch");
        Check(Filter("exercise").Contains("2026-10-11T01:00:00", StringComparison.Ordinal), "Civil filters must cover every recorded timezone");
        Check(Filter("sleep").StartsWith("sleep.interval.end_time >=", StringComparison.Ordinal), "Sleep must use wake time");
        Check(!Filter("electrocardiogram").Contains(" AND ", StringComparison.Ordinal), "ECG API only supports a lower boundary");
        Check(HealthDataCatalog.Types.Single(type => type.Id == "food").Filter(window, "Asia/Tokyo") is null, "Food has no time filter");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}