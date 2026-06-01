using System;
using System.Collections.Generic;

public static class ExpirationDateGenerator
{
    public static IReadOnlyList<DateTime> Generate(
        DateTime referenceDate,
        int count,
        int minimumMonths,
        int maximumMonths,
        int seed
    )
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");
        }

        if (minimumMonths < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumMonths),
                "Minimum months must be zero or greater."
            );
        }

        if (maximumMonths < minimumMonths)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMonths),
                "Maximum months must be greater than or equal to minimum months."
            );
        }

        DateTime firstDate = referenceDate.Date.AddMonths(minimumMonths);
        DateTime lastDate = referenceDate.Date.AddMonths(maximumMonths);
        int availableDayCount = (lastDate - firstDate).Days + 1;

        if (count > availableDayCount)
        {
            throw new ArgumentException(
                $"Cannot generate {count} unique dates from a range containing {availableDayCount} days.",
                nameof(count)
            );
        }

        Random random = new(seed);
        HashSet<int> selectedOffsets = new();
        while (selectedOffsets.Count < count)
        {
            selectedOffsets.Add(random.Next(availableDayCount));
        }

        List<DateTime> dates = new(selectedOffsets.Count);
        foreach (int offset in selectedOffsets)
        {
            dates.Add(firstDate.AddDays(offset));
        }

        dates.Sort();
        return dates;
    }
}
