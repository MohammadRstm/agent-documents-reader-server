using DocumentReader.Api.Analysis;

namespace DocumentReader.Tests.Analysis;

public sealed class DateFinderTests
{
    [Theory]
    [InlineData("Handover on 2027-03-15.", 2027, 3, 15)]
    [InlineData("Handover on 15/03/2027.", 2027, 3, 15)]
    [InlineData("Handover on 15.03.2027.", 2027, 3, 15)]
    [InlineData("Handover on 03/15/2027.", 2027, 3, 15)]
    [InlineData("Handover on 15 March 2027.", 2027, 3, 15)]
    [InlineData("Handover on the 15th of Mar 2027.", 2027, 3, 15)]
    [InlineData("Handover on March 15, 2027.", 2027, 3, 15)]
    [InlineData("Excel cell 2027-03-15 10:30:00", 2027, 3, 15)]
    public void Find_RecognizesUnambiguousFormats(string text, int year, int month, int day)
    {
        var found = Assert.Single(DateFinder.Find(text));

        Assert.Equal(new DateOnly(year, month, day), found.Date);
        Assert.False(found.IsAmbiguous);
    }

    [Fact]
    public void Find_AmbiguousNumericDate_ReturnsBothReadings()
    {
        var found = Assert.Single(DateFinder.Find("Due 03/04/2027"));

        Assert.True(found.IsAmbiguous);
        Assert.Null(found.Date);
        Assert.Equal([new DateOnly(2027, 4, 3), new DateOnly(2027, 3, 4)], found.Candidates);
    }

    [Theory]
    [InlineData("2027-02-30")]
    [InlineData("31/02/2027")]
    [InlineData("Version 1.2.3")]
    [InlineData("Within 30 days")]
    [InlineData("Phone 12-34-5678")]
    [InlineData("Year 1850-01-01")]
    public void Find_IgnoresInvalidOrNonDates(string text)
    {
        Assert.Empty(DateFinder.Find(text));
    }

    [Fact]
    public void Find_MultipleDates_AreReturnedInTextOrder()
    {
        var found = DateFinder.Find("Start 1 January 2027, finish 2027-12-31.");

        Assert.Equal([new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31)], found.Select(date => date.Date!.Value));
    }
}
