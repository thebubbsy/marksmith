using MarkSmith.ViewModels.History;
using Xunit;

namespace MarkSmith.Tests;

public class HistoryTimelineTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 15, 0, 0); // Thursday

    [Fact]
    public void BandNames_RunFromTodayToOlder()
    {
        Assert.Equal("Today", HistoryWindowViewModel.BandName(Now.AddHours(-1), Now));
        Assert.Equal("Yesterday", HistoryWindowViewModel.BandName(Now.AddDays(-1), Now));
        Assert.Equal("This Week", HistoryWindowViewModel.BandName(Now.AddDays(-3), Now));
        // Same calendar month but outside the rolling 7-day window -> This Month.
        Assert.Equal("This Month", HistoryWindowViewModel.BandName(new DateTime(2026, 8, 10, 9, 0, 0), new DateTime(2026, 8, 20, 15, 0, 0)));
        Assert.Equal("This Year", HistoryWindowViewModel.BandName(new DateTime(2026, 1, 10), Now));
        Assert.Equal("Older", HistoryWindowViewModel.BandName(new DateTime(2025, 12, 31), Now));
    }

    [Fact]
    public void TimestampLabels_GetMoreDetailedTheNewerTheVersion()
    {
        // Today: time only.
        Assert.Equal("14:00", HistoryWindowViewModel.TimestampLabel(Now.AddHours(-1), Now));
        // Yesterday: day + time.
        Assert.StartsWith("Yesterday", HistoryWindowViewModel.TimestampLabel(Now.AddDays(-1), Now));
        // This week: weekday + time.
        Assert.Contains("Monday", HistoryWindowViewModel.TimestampLabel(new DateTime(2026, 8, 3, 9, 0, 0), Now));
        // Same month (outside the week window): date + time.
        Assert.Equal("10 Aug · 10:30", HistoryWindowViewModel.TimestampLabel(new DateTime(2026, 8, 10, 10, 30, 0), new DateTime(2026, 8, 20, 15, 0, 0)));
        // Same year, earlier month: date only.
        Assert.Equal("20 Jan", HistoryWindowViewModel.TimestampLabel(new DateTime(2026, 1, 20, 10, 30, 0), Now));
        // Older: full date with year.
        Assert.Equal("31 Dec 2025", HistoryWindowViewModel.TimestampLabel(new DateTime(2025, 12, 31, 8, 0, 0), Now));
    }
}
