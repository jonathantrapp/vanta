namespace HwSuite.Ui;

/// <summary>What a UI fixture's Prepare step may do to the main window (the WPF build passes its MainWindow directly).</summary>
public interface IFixtureWindow { IFixtureLightingView LightingView { get; } }
public interface IFixtureLightingView { void SetArranging(bool on); }
