using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Case-insensitive helpers for reading "-flag value" style process arguments.
/// </summary>
public static class CommandLineArgs
{
    public static bool Has(string flag)
    {
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string Get(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    public static float GetFloat(string flag, float fallback, float minimum, float maximum)
    {
        return float.TryParse(Get(flag), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? Mathf.Clamp(parsed, minimum, maximum)
            : fallback;
    }
}
