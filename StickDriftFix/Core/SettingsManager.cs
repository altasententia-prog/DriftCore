using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriftCore.Core;

/// <summary>
/// All user-configurable settings. Serialised to JSON on disk.
/// </summary>
public class AppSettings
{
    public float DeadzoneRadius    { get; set; } = 0.08f;
    public float HysteresisMargin  { get; set; } = 0.025f;
    public float SmoothingFactor   { get; set; } = 0.30f;
    public float LearningRate      { get; set; } = 0.0008f;
    public float SpikeThreshold    { get; set; } = 0.35f;
    public bool  CorrectionEnabled { get; set; } = true;

    // Window placement
    public double WindowLeft   { get; set; } = double.NaN;
    public double WindowTop    { get; set; } = double.NaN;
    public double WindowWidth  { get; set; } = 980;
    public double WindowHeight { get; set; } = 720;
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to
/// %APPDATA%\DriftCore\settings.json.
/// </summary>
public static class SettingsManager
{
    private static readonly string _settingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "DriftCore");

    private static readonly string _settingsPath =
        Path.Combine(_settingsDir, "settings.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented        = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Load settings from disk. Returns defaults if the file does not exist
    /// or cannot be parsed.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();

            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions)
                   ?? new AppSettings();
        }
        catch
        {
            // Corrupted file — return defaults silently
            return new AppSettings();
        }
    }

    /// <summary>
    /// Persist settings to disk. Silently swallows IO errors
    /// (never crash the app over a settings save failure).
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_settingsDir);
            string json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Intentionally swallowed
        }
    }
}
