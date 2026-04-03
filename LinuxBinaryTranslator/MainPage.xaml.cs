// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Main page for the Linux Binary Translator UWP app.
// Provides a terminal-style UI with gamepad support for Xbox One.
// Handles ELF binary loading via file picker, execution control,
// and terminal I/O bridging.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using LinuxBinaryTranslator.FileSystem;
using LinuxBinaryTranslator.Terminal;

namespace LinuxBinaryTranslator
{
    /// <summary>
    /// Main page hosting the terminal emulator UI.
    /// Designed for both desktop and Xbox One gamepad interaction.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private TerminalEmulator? _terminal;
        private ExecutionEngine? _engine;
        private CancellationTokenSource? _cts;
        private readonly StringBuilder _displayBuffer = new StringBuilder();

        // Rate-limit UI updates for performance
        private DispatcherTimer? _uiUpdateTimer;
        private bool _outputDirty;

        public MainPage()
        {
            this.InitializeComponent();

            // Set up a timer to batch UI updates (16ms ≈ 60fps)
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            _uiUpdateTimer.Start();
        }

        /// <summary>
        /// Load an ELF binary from the file picker.
        /// </summary>
        private async void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await LoadAndRunAsync(file);
        }

        /// <summary>
        /// Load and execute an ELF binary file.
        /// </summary>
        private async Task LoadAndRunAsync(StorageFile file)
        {
            // Stop any running execution
            StopExecution();

            // Read the file
            var buffer = await FileIO.ReadBufferAsync(file);
            byte[] elfData = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(elfData);
            }

            // Clear the display
            _displayBuffer.Clear();
            _displayBuffer.AppendLine($"Loading: {file.Name} ({elfData.Length} bytes)");
            UpdateDisplay();

            // Set up terminal emulator
            _terminal = new TerminalEmulator();
            _terminal.OutputReceived += Terminal_OutputReceived;

            // Set up the execution engine
            _engine = new ExecutionEngine(
                stdinRead: _terminal.ReadInput,
                stdoutWrite: _terminal.WriteOutput,
                stderrWrite: _terminal.WriteOutput,
                logger: LogMessage);

            try
            {
                // Load the ELF binary
                var loadResult = _engine.LoadBinary(elfData, new[] { file.Name });
                AppendOutput($"Entry point: 0x{loadResult.EntryPoint:X16}\n");
                AppendOutput($"Segments: {loadResult.Segments.Count}\n");
                AppendOutput($"Machine: {(loadResult.Machine == Elf.ElfConstants.EM_X86_64 ? "x86_64" : $"0x{loadResult.Machine:X}")}\n");
                AppendOutput("--- Execution started ---\n");

                UpdateStatus("Running");
                LoadButton.IsEnabled = false;
                StopButton.IsEnabled = true;

                // Execute
                _cts = new CancellationTokenSource();
                var result = await _engine.ExecuteAsync(_cts.Token);

                // Display results
                AppendOutput($"\n--- Execution finished ---\n");
                AppendOutput($"Exit code: {result.ExitCode}\n");
                AppendOutput($"Blocks executed: {result.InstructionBlocksExecuted:N0}\n");
                AppendOutput($"Elapsed: {result.ElapsedTime.TotalMilliseconds:F1} ms\n");
                if (result.Error != null)
                    AppendOutput($"Error: {result.Error}\n");

                UpdateStatus($"Exited ({result.ExitCode})");
                UpdatePerformance($"{result.InstructionBlocksExecuted:N0} blocks in {result.ElapsedTime.TotalMilliseconds:F0}ms");
            }
            catch (Elf.ElfLoadException ex)
            {
                AppendOutput($"ELF load error: {ex.Message}\n");
                UpdateStatus("Load failed");
            }
            catch (Exception ex)
            {
                AppendOutput($"Error: {ex.Message}\n");
                UpdateStatus("Error");
            }
            finally
            {
                LoadButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }

        /// <summary>
        /// Boot a Linux distribution from a rootfs folder.
        /// The rootfs folder should be in app local storage or picked via folder picker.
        /// </summary>
        private async void BootRootfsButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");

            StorageFolder folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            await BootRootfsAsync(folder);
        }

        /// <summary>
        /// Load a rootfs from a folder and boot its default shell.
        /// </summary>
        private async Task BootRootfsAsync(StorageFolder rootfsFolder)
        {
            StopExecution();

            _displayBuffer.Clear();
            _displayBuffer.AppendLine($"Loading rootfs from: {rootfsFolder.Path}");
            _displayBuffer.AppendLine("Scanning files...");
            UpdateDisplay();

            // Set up terminal emulator
            _terminal = new TerminalEmulator();
            _terminal.OutputReceived += Terminal_OutputReceived;

            // Set up the execution engine
            _engine = new ExecutionEngine(
                stdinRead: _terminal.ReadInput,
                stdoutWrite: _terminal.WriteOutput,
                stderrWrite: _terminal.WriteOutput,
                logger: LogMessage);

            try
            {
                // Scan the rootfs folder and load files into memory
                var fileMap = new Dictionary<string, byte[]>();
                int fileCount = 0;
                long totalBytes = 0;

                await Task.Run(async () =>
                {
                    await ScanFolderRecursive(rootfsFolder, "", fileMap);
                });

                foreach (var kvp in fileMap)
                {
                    fileCount++;
                    totalBytes += kvp.Value.Length;
                }

                AppendOutput($"Found {fileCount} files ({totalBytes / 1024}KB)\n");
                AppendOutput("Mounting rootfs...\n");

                // Create rootfs manager and load the file map
                var rootfs = new RootfsManager(_engine.Vfs, LogMessage);
                var info = rootfs.LoadFromFileMap(fileMap);

                AppendOutput($"Distro: {info.DistroName} {info.Version}\n");
                AppendOutput($"Shell: {info.ShellPath}\n");
                AppendOutput($"Files: {info.FileCount}, Size: {info.TotalSize / 1024}KB\n");
                AppendOutput("--- Booting ---\n\n");

                UpdateStatus($"Booting {info.DistroName}");
                LoadButton.IsEnabled = false;
                BootRootfsButton.IsEnabled = false;
                StopButton.IsEnabled = true;

                // Boot the rootfs (loads the shell binary)
                var loadResult = _engine.BootRootfs(rootfs);

                AppendOutput($"Entry point: 0x{loadResult.EntryPoint:X16}\n");

                // Execute
                _cts = new CancellationTokenSource();
                var result = await _engine.ExecuteAsync(_cts.Token);

                // Display results
                AppendOutput($"\n--- Session ended ---\n");
                AppendOutput($"Exit code: {result.ExitCode}\n");
                AppendOutput($"Blocks executed: {result.InstructionBlocksExecuted:N0}\n");
                AppendOutput($"Elapsed: {result.ElapsedTime.TotalMilliseconds:F1} ms\n");
                if (result.Error != null)
                    AppendOutput($"Error: {result.Error}\n");

                UpdateStatus($"Exited ({result.ExitCode})");
                UpdatePerformance($"{result.InstructionBlocksExecuted:N0} blocks in {result.ElapsedTime.TotalMilliseconds:F0}ms");
            }
            catch (Elf.ElfLoadException ex)
            {
                AppendOutput($"ELF load error: {ex.Message}\n");
                UpdateStatus("Boot failed");
            }
            catch (Exception ex)
            {
                AppendOutput($"Error: {ex.Message}\n");
                UpdateStatus("Error");
            }
            finally
            {
                LoadButton.IsEnabled = true;
                BootRootfsButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }

        /// <summary>
        /// Recursively scan a folder and load all files into a path→data map.
        /// Paths are relative to the rootfs root (e.g., "/bin/bash").
        /// </summary>
        private async Task ScanFolderRecursive(StorageFolder folder, string prefix,
                                                Dictionary<string, byte[]> fileMap)
        {
            // Get all files in this folder
            var files = await folder.GetFilesAsync();
            foreach (var file in files)
            {
                try
                {
                    var buffer = await FileIO.ReadBufferAsync(file);
                    byte[] data = new byte[buffer.Length];
                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                    {
                        reader.ReadBytes(data);
                    }
                    string path = prefix + "/" + file.Name;
                    fileMap[path] = data;
                }
                catch
                {
                    // Skip files that can't be read
                }
            }

            // Recurse into subfolders
            var subFolders = await folder.GetFoldersAsync();
            foreach (var sub in subFolders)
            {
                string subPrefix = prefix + "/" + sub.Name;
                await ScanFolderRecursive(sub, subPrefix, fileMap);
            }
        }

        /// <summary>
        /// Stop the currently running binary.
        /// </summary>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopExecution();
        }

        private void StopExecution()
        {
            _cts?.Cancel();
            _cts = null;
            if (_engine != null)
            {
                _engine.Cpu.Halted = true;
                _engine = null;
            }
        }

        /// <summary>
        /// Handle input box Enter key to send input to the process.
        /// </summary>
        private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                SendInput();
                e.Handled = true;
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendInput();
        }

        private void SendInput()
        {
            string text = InputBox.Text;
            if (_terminal != null)
            {
                _terminal.SendLine(text);
                AppendOutput($"$ {text}\n");
            }
            InputBox.Text = "";
        }

        /// <summary>
        /// Handle page-level key events for Xbox gamepad button mapping.
        /// </summary>
        private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_terminal == null) return;

            switch (e.Key)
            {
                // Xbox B button or Escape → Ctrl+C
                case VirtualKey.Escape:
                case VirtualKey.GamepadB:
                    _terminal.SendKey(TerminalKey.CtrlC);
                    e.Handled = true;
                    break;

                // Xbox X button → Load binary
                case VirtualKey.GamepadX:
                    LoadButton_Click(sender, e);
                    e.Handled = true;
                    break;

                // Xbox Y button → Clear terminal
                case VirtualKey.GamepadY:
                    _displayBuffer.Clear();
                    UpdateDisplay();
                    e.Handled = true;
                    break;

                // Xbox A button → Submit input
                case VirtualKey.GamepadA:
                    SendInput();
                    e.Handled = true;
                    break;

                // DPad → Arrow keys
                case VirtualKey.GamepadDPadUp:
                    _terminal.SendKey(TerminalKey.Up);
                    e.Handled = true;
                    break;
                case VirtualKey.GamepadDPadDown:
                    _terminal.SendKey(TerminalKey.Down);
                    e.Handled = true;
                    break;
                case VirtualKey.GamepadDPadLeft:
                    _terminal.SendKey(TerminalKey.Left);
                    e.Handled = true;
                    break;
                case VirtualKey.GamepadDPadRight:
                    _terminal.SendKey(TerminalKey.Right);
                    e.Handled = true;
                    break;

                // LB → Tab, RB → Backspace
                case VirtualKey.GamepadLeftShoulder:
                    _terminal.SendKey(TerminalKey.Tab);
                    e.Handled = true;
                    break;
                case VirtualKey.GamepadRightShoulder:
                    _terminal.SendKey(TerminalKey.Backspace);
                    e.Handled = true;
                    break;
            }
        }

        // === Terminal output handling ===

        private void Terminal_OutputReceived(object? sender, TerminalOutputEventArgs e)
        {
            _outputDirty = true;
        }

        private void UiUpdateTimer_Tick(object? sender, object e)
        {
            if (!_outputDirty || _terminal == null) return;
            _outputDirty = false;

            string newText = _terminal.FlushOutput();
            if (newText.Length > 0)
            {
                _displayBuffer.Append(newText);

                // Cap total display size
                if (_displayBuffer.Length > 512 * 1024)
                    _displayBuffer.Remove(0, _displayBuffer.Length - 256 * 1024);

                UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            OutputTextBlock.Text = _displayBuffer.ToString();
            // Auto-scroll to bottom
            OutputScrollViewer.ChangeView(null, OutputScrollViewer.ScrollableHeight, null);
        }

        private void AppendOutput(string text)
        {
            _displayBuffer.Append(text);
            _outputDirty = true;
        }

        private void UpdateStatus(string status)
        {
            StatusText.Text = status;
        }

        private void UpdatePerformance(string text)
        {
            PerformanceText.Text = text;
        }

        private void LogMessage(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[LBT] {message}");
        }
    }
}
