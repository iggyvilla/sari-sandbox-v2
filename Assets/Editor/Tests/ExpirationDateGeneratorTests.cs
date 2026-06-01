using System;
using System.Collections.Generic;
using NUnit.Framework;

public class ExpirationDateGeneratorTests
{
    [Test]
    public void Generate_WithSameInputs_ReturnsSameDates()
    {
        DateTime referenceDate = new(2026, 6, 1);

        IReadOnlyList<DateTime> first = ExpirationDateGenerator.Generate(referenceDate, 100, 1, 12, 42);
        IReadOnlyList<DateTime> second = ExpirationDateGenerator.Generate(referenceDate, 100, 1, 12, 42);

        CollectionAssert.AreEqual(first, second);
    }

    [Test]
    public void Generate_ReturnsUniqueDatesWithinInclusiveRange()
    {
        DateTime referenceDate = new(2026, 1, 31);
        DateTime expectedFirstDate = referenceDate.AddMonths(1);
        DateTime expectedLastDate = referenceDate.AddMonths(2);
        int availableDayCount = (expectedLastDate - expectedFirstDate).Days + 1;

        IReadOnlyList<DateTime> dates =
            ExpirationDateGenerator.Generate(referenceDate, availableDayCount, 1, 2, 123);

        Assert.That(dates, Has.Count.EqualTo(availableDayCount));
        Assert.That(new HashSet<DateTime>(dates), Has.Count.EqualTo(availableDayCount));
        Assert.That(dates[0], Is.EqualTo(expectedFirstDate));
        Assert.That(dates[dates.Count - 1], Is.EqualTo(expectedLastDate));
    }

    [Test]
    public void Generate_WithNonPositiveCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExpirationDateGenerator.Generate(new DateTime(2026, 6, 1), 0, 1, 12, 0)
        );
    }

    [Test]
    public void Generate_WithNegativeMinimumMonths_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExpirationDateGenerator.Generate(new DateTime(2026, 6, 1), 1, -1, 12, 0)
        );
    }

    [Test]
    public void Generate_WithReversedRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExpirationDateGenerator.Generate(new DateTime(2026, 6, 1), 1, 12, 1, 0)
        );
    }

    [Test]
    public void Generate_WithInsufficientDays_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ExpirationDateGenerator.Generate(new DateTime(2026, 6, 1), 2, 1, 1, 0)
        );
    }

    [Test]
    public void FormatExpirationDate_UsesDayMonthYear()
    {
        Assert.That(
            ExpirationDateDecalHandler.FormatExpirationDate(new DateTime(2027, 12, 31)),
            Is.EqualTo("31/12/2027")
        );
    }
}
