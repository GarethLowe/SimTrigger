namespace SimLauncher.Core.Config;

public static class ConfigValidator
{
    /// <summary>Returns human-readable problems; empty list means the config is usable.</summary>
    public static IReadOnlyList<string> Validate(LauncherConfig config)
    {
        var errors = new List<string>();

        if (config.Profiles.Count == 0)
        {
            errors.Add("Config contains no profiles.");
            return errors;
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                errors.Add("A profile has an empty name.");
            }
            else if (!profileNames.Add(profile.Name))
            {
                errors.Add($"Duplicate profile name '{profile.Name}'.");
            }

            ValidateProfile(profile, errors);
        }

        if (!string.IsNullOrEmpty(config.ActiveProfile)
            && config.Profiles.All(p => p.Name != config.ActiveProfile))
        {
            errors.Add($"Active profile '{config.ActiveProfile}' does not exist.");
        }

        if (config.SimConnection.PollIntervalSeconds < 1)
        {
            errors.Add("simConnection.pollIntervalSeconds must be at least 1.");
        }
        if (config.SimConnection.DisconnectGraceSeconds < 0)
        {
            errors.Add("simConnection.disconnectGraceSeconds must not be negative.");
        }
        if (config.SimConnection.DebounceSeconds < 0)
        {
            errors.Add("simConnection.debounceSeconds must not be negative.");
        }


        return errors;
    }

    private static void ValidateProfile(ProfileConfig profile, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in profile.Apps)
        {
            var where = $"Profile '{profile.Name}', app '{app.Name}'";

            if (string.IsNullOrWhiteSpace(app.Name))
            {
                errors.Add($"Profile '{profile.Name}' has an app with an empty name.");
            }
            else if (!names.Add(app.Name))
            {
                errors.Add($"Profile '{profile.Name}' has duplicate app name '{app.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(app.Path))
            {
                errors.Add($"{where}: path is empty.");
            }
            if (app.DelaySeconds < 0)
            {
                errors.Add($"{where}: delaySeconds must not be negative.");
            }
            if (app.WaitForAppReadySeconds < 0)
            {
                errors.Add($"{where}: waitForAppReadySeconds must not be negative.");
            }
            if (app.ShutdownTimeoutSeconds < 0)
            {
                errors.Add($"{where}: shutdownTimeoutSeconds must not be negative.");
            }

            if (!string.IsNullOrEmpty(app.WaitForApp))
            {
                var dep = profile.Apps.FirstOrDefault(a =>
                    string.Equals(a.Name, app.WaitForApp, StringComparison.OrdinalIgnoreCase));
                if (dep is null)
                {
                    errors.Add($"{where}: waitForApp '{app.WaitForApp}' does not exist in the profile.");
                }
                else if (dep.Checkpoint != app.Checkpoint)
                {
                    errors.Add($"{where}: waitForApp '{app.WaitForApp}' is at a different checkpoint ({dep.Checkpoint}); ordering only applies within a checkpoint.");
                }
            }
        }

        // Detect waitForApp cycles per checkpoint.
        foreach (var group in profile.Apps.GroupBy(a => a.Checkpoint))
        {
            var byName = group
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var app in group)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var current = app;
                while (!string.IsNullOrEmpty(current.WaitForApp)
                       && byName.TryGetValue(current.WaitForApp, out var next))
                {
                    if (!seen.Add(current.Name))
                    {
                        errors.Add($"Profile '{profile.Name}': waitForApp cycle involving '{app.Name}' at {group.Key}.");
                        break;
                    }
                    current = next;
                }
            }
        }
    }
}
