using System.Configuration;
using System.Data;
using System.Windows;
using System.Diagnostics;
using System;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace ProjectionMapper
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                Debug.WriteLine("App: Starting application initialization");

                // Add global exception handlers to catch startup issues
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                DispatcherUnhandledException += OnDispatcherUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

                // Verify runtime environment before proceeding
                if (!VerifyRuntimeEnvironment())
                {
                    Debug.WriteLine("App: Runtime environment verification failed");
                    MessageBox.Show(
                        "Runtime environment verification failed. Please ensure .NET 9 is properly installed.",
                        "Startup Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }

                Debug.WriteLine("App: Runtime environment verified, calling base.OnStartup");
                base.OnStartup(e);
                Debug.WriteLine("App: Application startup completed successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"App: Critical startup failure: {ex}");
                
                try
                {
                    MessageBox.Show(
                        $"Application failed to start: {ex.Message}\n\nSee debug output for details.",
                        "Critical Startup Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                    // If we can't even show a message box, write to debug and exit
                    Debug.WriteLine("App: Unable to show error dialog, exiting");
                }
                
                Shutdown(3);
            }
        }

        private bool VerifyRuntimeEnvironment()
        {
            try
            {
                // Check basic .NET functionality
                Debug.WriteLine($"App: Runtime version: {Environment.Version}");
                Debug.WriteLine($"App: OS version: {Environment.OSVersion}");
                Debug.WriteLine($"App: 64-bit process: {Environment.Is64BitProcess}");
                Debug.WriteLine($"App: Working directory: {Environment.CurrentDirectory}");

                // Verify WPF is available
                var testDispatcher = Dispatcher.CurrentDispatcher;
                if (testDispatcher == null)
                {
                    Debug.WriteLine("App: WPF Dispatcher not available");
                    return false;
                }

                Debug.WriteLine("App: Basic runtime verification passed");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"App: Runtime verification failed: {ex}");
                return false;
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                Debug.WriteLine($"App: Unhandled exception: {ex?.ToString() ?? "Unknown exception"}");
                Debug.WriteLine($"App: Is terminating: {e.IsTerminating}");

                if (e.IsTerminating)
                {
                    // Try to log the error before the process terminates
                    try
                    {
                        MessageBox.Show(
                            $"A critical error occurred: {ex?.Message ?? "Unknown error"}\n\nThe application will now exit.",
                            "Critical Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    catch
                    {
                        // Can't show UI, just debug output
                        Debug.WriteLine("App: Unable to show critical error dialog");
                    }
                }
            }
            catch (Exception loggingEx)
            {
                Debug.WriteLine($"App: Error in exception handler: {loggingEx}");
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Debug.WriteLine($"App: Dispatcher unhandled exception: {e.Exception}");

                // Try to handle the exception gracefully
                try
                {
                    MessageBox.Show(
                        $"An error occurred: {e.Exception.Message}\n\nThe operation has been cancelled.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    e.Handled = true; // Prevent application crash
                }
                catch
                {
                    Debug.WriteLine("App: Unable to handle dispatcher exception gracefully");
                    // Let the exception bubble up
                }
            }
            catch (Exception loggingEx)
            {
                Debug.WriteLine($"App: Error in dispatcher exception handler: {loggingEx}");
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                Debug.WriteLine($"App: Unobserved task exception: {e.Exception}");
                e.SetObserved(); // Prevent process termination
            }
            catch (Exception loggingEx)
            {
                Debug.WriteLine($"App: Error in task exception handler: {loggingEx}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Debug.WriteLine($"App: Application exiting with code {e.ApplicationExitCode}");
                base.OnExit(e);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"App: Error during exit: {ex}");
            }
        }
    }
}
