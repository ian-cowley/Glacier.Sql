namespace Glacier.Sql.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using Glacier.Sql.Catalog;
using Glacier.Sql.Diagnostics;
using Xunit;

public class DiagnosticsTests
{
    [Fact]
    public void GlacierDiagnostics_DefaultLogger_IsNullLogger()
    {
        GlacierDiagnostics.Reset();
        Assert.Same(NullGlacierLogger.Instance, GlacierDiagnostics.Logger);
        Assert.Same(NullGlacierLogger.Instance, GlacierSqlDiagnostics.Logger);
        Assert.False(GlacierDiagnostics.IsEnabled(LogLevel.Trace));
        Assert.False(GlacierDiagnostics.IsEnabled(LogLevel.Debug));
        Assert.False(GlacierDiagnostics.IsEnabled(LogLevel.Information));
        Assert.False(GlacierDiagnostics.IsEnabled(LogLevel.Warning));
        Assert.False(GlacierDiagnostics.IsEnabled(LogLevel.Error));
        Assert.False(GlacierDiagnostics.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void GlacierDiagnostics_SetLogger_And_Reset_Works()
    {
        var messages = new List<(LogLevel Level, string Message, Exception? Ex)>();
        var testLogger = new DelegateGlacierLogger((level, msg, ex) =>
        {
            messages.Add((level, msg, ex));
        }, LogLevel.Trace);

        try
        {
            GlacierDiagnostics.SetLogger(testLogger);
            Assert.Same(testLogger, GlacierDiagnostics.Logger);
            Assert.Same(testLogger, GlacierSqlDiagnostics.Logger);
            Assert.True(GlacierDiagnostics.IsEnabled(LogLevel.Information));

            GlacierDiagnostics.LogInformation("SQL Catalog initialized");
            Assert.Single(messages);
            Assert.Equal(LogLevel.Information, messages[0].Level);
            Assert.Equal("SQL Catalog initialized", messages[0].Message);
            Assert.Null(messages[0].Ex);

            GlacierDiagnostics.Reset();
            Assert.Same(NullGlacierLogger.Instance, GlacierDiagnostics.Logger);

            GlacierDiagnostics.LogInformation("Should not be recorded");
            Assert.Single(messages); // Count unchanged
        }
        finally
        {
            GlacierDiagnostics.Reset();
        }
    }

    [Fact]
    public void GlacierDiagnostics_ConvenienceMethods_DispatchAllLevels()
    {
        var messages = new List<(LogLevel Level, string Message, Exception? Ex)>();
        var testLogger = new DelegateGlacierLogger((level, msg, ex) =>
        {
            messages.Add((level, msg, ex));
        }, LogLevel.Trace);

        try
        {
            GlacierDiagnostics.Logger = testLogger;

            var testEx = new InvalidOperationException("Test disk I/O failure");
            GlacierDiagnostics.Log(LogLevel.Information, "direct log message");
            GlacierDiagnostics.LogTrace("trace message");
            GlacierDiagnostics.LogDebug("debug message");
            GlacierDiagnostics.LogInformation("info message");
            GlacierDiagnostics.LogWarning("warn message", testEx);
            GlacierDiagnostics.LogError("error message", testEx);
            GlacierDiagnostics.LogCritical("critical message", testEx);

            Assert.Equal(7, messages.Count);
            Assert.Equal(LogLevel.Information, messages[0].Level);
            Assert.Equal("direct log message", messages[0].Message);

            Assert.Equal(LogLevel.Trace, messages[1].Level);
            Assert.Equal("trace message", messages[1].Message);

            Assert.Equal(LogLevel.Debug, messages[2].Level);
            Assert.Equal("debug message", messages[2].Message);

            Assert.Equal(LogLevel.Information, messages[3].Level);
            Assert.Equal("info message", messages[3].Message);

            Assert.Equal(LogLevel.Warning, messages[4].Level);
            Assert.Equal("warn message", messages[4].Message);
            Assert.Same(testEx, messages[4].Ex);

            Assert.Equal(LogLevel.Error, messages[5].Level);
            Assert.Equal("error message", messages[5].Message);
            Assert.Same(testEx, messages[5].Ex);

            Assert.Equal(LogLevel.Critical, messages[6].Level);
            Assert.Equal("critical message", messages[6].Message);
            Assert.Same(testEx, messages[6].Ex);
        }
        finally
        {
            GlacierDiagnostics.Reset();
        }
    }

    [Fact]
    public void GlacierSqlDiagnostics_Alias_MatchesGlacierDiagnostics()
    {
        var messages = new List<(LogLevel Level, string Message, Exception? Ex)>();
        var testLogger = new DelegateGlacierLogger((level, msg, ex) =>
        {
            messages.Add((level, msg, ex));
        }, LogLevel.Trace);

        try
        {
            GlacierSqlDiagnostics.SetLogger(testLogger);
            Assert.Same(testLogger, GlacierDiagnostics.Logger);
            Assert.Same(testLogger, GlacierSqlDiagnostics.Logger);

            var ex = new Exception("Catalog error");
            GlacierSqlDiagnostics.Log(LogLevel.Information, "alias log");
            GlacierSqlDiagnostics.LogTrace("alias trace");
            GlacierSqlDiagnostics.LogDebug("alias debug");
            GlacierSqlDiagnostics.LogInformation("alias info");
            GlacierSqlDiagnostics.LogWarning("alias warn", ex);
            GlacierSqlDiagnostics.LogError("alias error", ex);
            GlacierSqlDiagnostics.LogCritical("alias crit", ex);

            Assert.Equal(7, messages.Count);
            Assert.Equal("alias log", messages[0].Message);
            Assert.Equal("alias trace", messages[1].Message);
            Assert.Equal("alias debug", messages[2].Message);
            Assert.Equal("alias info", messages[3].Message);
            Assert.Equal("alias warn", messages[4].Message);
            Assert.Equal("alias error", messages[5].Message);
            Assert.Equal("alias crit", messages[6].Message);

            GlacierSqlDiagnostics.Reset();
            Assert.Same(NullGlacierLogger.Instance, GlacierSqlDiagnostics.Logger);
            Assert.Same(NullGlacierLogger.Instance, GlacierDiagnostics.Logger);
        }
        finally
        {
            GlacierSqlDiagnostics.Reset();
        }
    }

    [Fact]
    public void ConsoleGlacierLogger_FiltersByLogLevel()
    {
        var logger = new ConsoleGlacierLogger(LogLevel.Warning, useColors: false);

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
        Assert.False(logger.IsEnabled(LogLevel.None));

        // Ensure logging executes safely without throwing
        logger.Log(LogLevel.Information, "Ignored info message");
        logger.Log(LogLevel.Warning, "Reported warning");
        logger.Log(LogLevel.Error, "Reported error", new Exception("Test"));
    }

    [Fact]
    public void DelegateGlacierLogger_StringOnlyConstructor_FormatsCorrectly()
    {
        var logEntries = new List<string>();
        var logger = new DelegateGlacierLogger(msg => logEntries.Add(msg), LogLevel.Debug);

        logger.Log(LogLevel.Debug, "Engine started");
        logger.Log(LogLevel.Error, "Disk failed", new Exception("Permission denied"));

        Assert.Equal(2, logEntries.Count);
        Assert.Equal("[Debug] Engine started", logEntries[0]);
        Assert.Contains("[Error] Disk failed:", logEntries[1]);
        Assert.Contains("Permission denied", logEntries[1]);
    }

    [Fact]
    public void DelegateGlacierLogger_RequiresNonNullAction()
    {
        Action<LogLevel, string, Exception?> nullAction = null!;
        Assert.Throws<ArgumentNullException>(() => new DelegateGlacierLogger(nullAction));
    }

    [Fact]
    public void CatalogManager_ErrorHandling_DispatchesToAmbientDiagnostics()
    {
        var loggedErrors = new List<(LogLevel Level, string Message, Exception? Ex)>();
        var testLogger = new DelegateGlacierLogger((level, msg, ex) =>
        {
            loggedErrors.Add((level, msg, ex));
        }, LogLevel.Trace);

        string tempDir = Path.Combine(Path.GetTempPath(), "GlacierSqlTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            GlacierSqlDiagnostics.SetLogger(testLogger);

            Directory.CreateDirectory(tempDir);
            string invalidCatalogFile = Path.Combine(tempDir, "catalog.json");
            File.WriteAllText(invalidCatalogFile, "{ malformed json corrupt content");

            // Initializing CatalogManager on corrupted catalog file invokes Load() which catches and logs error
            var cm = new CatalogManager(tempDir);

            // Verify that the error was captured in ambient diagnostics
            Assert.Contains(loggedErrors, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Error loading catalog"));
        }
        finally
        {
            GlacierSqlDiagnostics.Reset();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
