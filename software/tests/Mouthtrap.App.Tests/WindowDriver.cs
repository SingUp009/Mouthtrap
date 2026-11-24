using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Mouthtrap.App;
using Xunit;

namespace Mouthtrap.App.Tests;

// Drives a real MainWindow the way a user or a screen reader does: controls are found in the UI Automation tree by
// their accessible name and control type, and are clicked, typed into and chosen through automation patterns, which
// refuse disabled controls as the mouse would. Waits follow property changes, never delays.
internal sealed class WindowDriver(MainWindow window)
{
    public MainWindow Window { get; } = window;

    // Opens the window off screen on an STA dispatcher in the given UI language, waits until it is ready for input,
    // runs the scenario, then closes the window the way the user does.
    public static Task Run(string settingsPath, string culture, Func<WindowDriver, Task> scenario)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Exception? failure = null;
                dispatcher.UnhandledException += (_, args) =>
                {
                    failure ??= args.Exception;
                    args.Handled = true;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var window = new MainWindow(settingsPath)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false,
                    ShowInTaskbar = false
                };
                window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                // After the window has loaded, which starts camera detection.
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
                {
                    try
                    {
                        var driver = new WindowDriver(window);
                        // Start ignores clicks while cameras are being detected, so wait until the window is idle.
                        var idle = Strings.Get("StateIdle");
                        await driver.UntilText(window.SessionStateText, text => text == idle);
                        await scenario(driver);
                    }
                    catch (Exception exception) { failure ??= exception; }
                    window.Close();
                });
                window.Show();
                Dispatcher.Run();
                if (failure is not null) throw failure;
                finished.TrySetResult();
            }
            catch (Exception exception) { finished.TrySetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    // The control with this accessible name and type, as UI Automation exposes it.
    public AutomationPeer Find(AutomationControlType type, string name)
    {
        var root = UIElementAutomationPeer.CreatePeerForElement(Window);
        return Search(root, type, name) ?? throw new Xunit.Sdk.XunitException($"No {type} named '{name}'.");
    }

    // UI Automation queues the click on the dispatcher, so this completes once the button has raised Click and the
    // window's handlers have run up to their first await.
    public Task Click(string buttonName)
    {
        var peer = (UIElementAutomationPeer)Find(AutomationControlType.Button, buttonName);
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? handler = null;
        handler = (_, _) =>
        {
            peer.Owner.RemoveHandler(ButtonBase.ClickEvent, handler);
            clicked.TrySetResult();
        };
        peer.Owner.AddHandler(ButtonBase.ClickEvent, handler, handledEventsToo: true);
        Pattern<IInvokeProvider>(peer, PatternInterface.Invoke).Invoke();
        return clicked.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    public void Toggle(string checkBoxName) =>
        Pattern<IToggleProvider>(Find(AutomationControlType.CheckBox, checkBoxName), PatternInterface.Toggle).Toggle();

    // Replaces the text and moves focus away, as a user does before pressing a button.
    public void Type(string editName, string text) =>
        Pattern<IValueProvider>(Find(AutomationControlType.Edit, editName), PatternInterface.Value).SetValue(text);

    public void Choose(string comboBoxName, string itemName)
    {
        var comboBox = Find(AutomationControlType.ComboBox, comboBoxName);
        Pattern<IExpandCollapseProvider>(comboBox, PatternInterface.ExpandCollapse).Expand();
        Window.UpdateLayout();
        var item = Search(comboBox, AutomationControlType.ListItem, itemName)
            ?? throw new Xunit.Sdk.XunitException($"'{comboBoxName}' has no item '{itemName}'.");
        Pattern<ISelectionItemProvider>(item, PatternInterface.SelectionItem).Select();
        Pattern<IExpandCollapseProvider>(comboBox, PatternInterface.ExpandCollapse).Collapse();
    }

    public bool IsEnabled(AutomationControlType type, string name) => Find(type, name).IsEnabled();

    // Completes when the condition holds, checking again whenever the watched property of the element changes.
    public Task Until(Func<bool> condition, DependencyObject element, DependencyProperty property)
    {
        if (condition()) return Task.CompletedTask;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
        EventHandler? changed = null;
        changed = (_, _) =>
        {
            if (!condition()) return;
            descriptor.RemoveValueChanged(element, changed);
            reached.TrySetResult();
        };
        descriptor.AddValueChanged(element, changed);
        return reached.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    // Completes when the text block shows text that meets the condition.
    public Task UntilText(TextBlock text, Func<string, bool> condition) =>
        Until(() => condition(text.Text), text, TextBlock.TextProperty);

    private static AutomationPeer? Search(AutomationPeer peer, AutomationControlType type, string name)
    {
        foreach (var child in peer.GetChildren() ?? [])
        {
            if (child.GetAutomationControlType() == type && child.GetName() == name) return child;
            if (Search(child, type, name) is { } found) return found;
        }
        return null;
    }

    private static T Pattern<T>(AutomationPeer peer, PatternInterface pattern) where T : class =>
        peer.GetPattern(pattern) as T ?? throw new Xunit.Sdk.XunitException($"'{peer.GetName()}' does not support {pattern}.");
}
