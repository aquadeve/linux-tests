// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Execution engine: orchestrates loading, translation, and running of
// Linux ELF binaries. This is the top-level coordinator that connects
// the ELF loader, block translator, syscall handler, and VFS.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LinuxBinaryTranslator.Cpu;
using LinuxBinaryTranslator.Cpu.Translation;
using LinuxBinaryTranslator.Elf;
using LinuxBinaryTranslator.FileSystem;
using LinuxBinaryTranslator.Memory;
using LinuxBinaryTranslator.Syscall;

namespace LinuxBinaryTranslator
{
    /// <summary>
    /// Result of a completed binary execution.
    /// </summary>
    public sealed class ExecutionResult
    {
        public int ExitCode { get; set; }
        public long InstructionBlocksExecuted { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Orchestrates the loading and execution of a Linux ELF binary.
    /// Connects all subsystems: ELF loader → block translator → syscall handler → VFS.
    /// </summary>
    public sealed class ExecutionEngine
    {
        private readonly VirtualMemoryManager _memory;
        private readonly VirtualFileSystem _vfs;
        private readonly BlockTranslator _translator;
        private readonly SyscallHandler _syscallHandler;
        private readonly CpuState _cpu;
        private readonly Action<string> _logger;

        // Stack configuration
        private const ulong StackBase = 0x7FFFFFFFE000UL;
        private const ulong StackSize = 8UL * 1024 * 1024; // 8 MB stack

        public CpuState Cpu => _cpu;
        public VirtualMemoryManager Memory => _memory;
        public VirtualFileSystem Vfs => _vfs;

        public ExecutionEngine(
            Func<byte[], int, int, int>? stdinRead,
            Action<byte[], int, int>? stdoutWrite,
            Action<byte[], int, int>? stderrWrite,
            Action<string>? logger = null)
        {
            _logger = logger ?? (_ => { });
            _memory = new VirtualMemoryManager();
            _vfs = new VirtualFileSystem(stdinRead, stdoutWrite, stderrWrite);
            _cpu = new CpuState();
            _translator = new BlockTranslator(_memory);
            _syscallHandler = new SyscallHandler(_memory, _vfs, _logger);
            _translator.SyscallHandler = _syscallHandler.Dispatch;
        }

        /// <summary>
        /// Load an ELF binary into memory and prepare it for execution.
        /// </summary>
        public ElfLoadResult LoadBinary(byte[] elfData, string[]? argv = null, string[]? envp = null)
        {
            var loader = new ElfLoader(_memory);
            var loadResult = loader.Load(elfData);

            _logger($"ELF loaded: entry=0x{loadResult.EntryPoint:X16}, " +
                    $"base=0x{loadResult.BaseAddress:X16}, " +
                    $"brk=0x{loadResult.BrkAddress:X16}, " +
                    $"segments={loadResult.Segments.Count}");

            // Initialize the program break
            _memory.InitializeBrk(loadResult.BrkAddress);

            // Set up the stack
            SetupStack(loadResult, argv ?? new[] { "program" }, envp ?? GetDefaultEnvironment());

            // Set the instruction pointer to the ELF entry point
            _cpu.RIP = loadResult.EntryPoint;

            return loadResult;
        }

        /// <summary>
        /// Execute the loaded binary until it exits or an error occurs.
        /// </summary>
        public async Task<ExecutionResult> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            long blocksExecuted = 0;
            string? error = null;

            try
            {
                await Task.Run(() =>
                {
                    while (!_cpu.Halted && !cancellationToken.IsCancellationRequested)
                    {
                        // Check for pending signals at safe points
                        if (_syscallHandler.DeliverPendingSignal(_cpu, _memory))
                        {
                            // Signal handler was set up — continue execution at new RIP
                            if (_cpu.Halted) break;
                            continue;
                        }

                        if (!_memory.IsMapped(_cpu.RIP))
                        {
                            // Try to deliver SIGSEGV before terminating
                            _logger($"Execution fault: RIP=0x{_cpu.RIP:X16} is not in mapped memory");
                            _syscallHandler.QueueSignal(11); // SIGSEGV
                            if (_syscallHandler.DeliverPendingSignal(_cpu, _memory))
                            {
                                if (_cpu.Halted) break;
                                continue; // Handler was set up
                            }
                            _cpu.Halted = true;
                            _cpu.ExitCode = 139; // SIGSEGV
                            break;
                        }

                        var block = _translator.GetBlock(_cpu.RIP);
                        block.ExecutionCount++;
                        blocksExecuted++;

                        ulong nextAddr = block.Execute(_cpu, _memory);

                        if (!_cpu.Halted && nextAddr != 0)
                            _cpu.RIP = nextAddr;

                        // Safety: check for infinite loops with a yield point
                        if (blocksExecuted % 100000 == 0)
                            Thread.Yield();
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                error = "Execution cancelled";
            }
            catch (Exception ex)
            {
                error = $"Execution error: {ex.Message}";
                _logger(error);
            }

            return new ExecutionResult
            {
                ExitCode = _cpu.ExitCode,
                InstructionBlocksExecuted = blocksExecuted,
                ElapsedTime = DateTime.UtcNow - startTime,
                Error = error,
            };
        }

        /// <summary>
        /// Set up the process stack with argc, argv, envp, and auxiliary vectors.
        /// This mirrors the Linux kernel's stack layout for ELF executables,
        /// as described in the kernel source (fs/binfmt_elf.c: create_elf_tables).
        ///
        /// Stack layout (growing downward):
        ///   [padding/alignment]
        ///   [auxv strings]
        ///   [envp strings]
        ///   [argv strings]
        ///   [NULL]
        ///   [auxv entries]
        ///   [NULL]
        ///   [envp pointers]
        ///   [NULL]
        ///   [argv pointers]
        ///   [argc]           ← RSP points here
        /// </summary>
        private void SetupStack(ElfLoadResult loadResult, string[] argv, string[] envp)
        {
            ulong stackTop = StackBase;
            ulong stackBottom = stackTop - StackSize;

            // Map the stack
            _memory.Map(stackBottom, StackSize, MemoryProtection.ReadWrite);

            ulong sp = stackTop;

            // Write string data at top of stack and collect pointers
            ulong[] argvPtrs = new ulong[argv.Length];
            for (int i = 0; i < argv.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(argv[i] + "\0");
                sp -= (ulong)bytes.Length;
                _memory.Write(sp, bytes);
                argvPtrs[i] = sp;
            }

            ulong[] envpPtrs = new ulong[envp.Length];
            for (int i = 0; i < envp.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(envp[i] + "\0");
                sp -= (ulong)bytes.Length;
                _memory.Write(sp, bytes);
                envpPtrs[i] = sp;
            }

            // Write 16 bytes of random data for AT_RANDOM
            sp -= 16;
            ulong randomAddr = sp;
            var rng = new Random();
            byte[] randomBytes = new byte[16];
            rng.NextBytes(randomBytes);
            _memory.Write(randomAddr, randomBytes);

            // Write "x86_64" platform string for AT_PLATFORM
            byte[] platformStr = Encoding.UTF8.GetBytes("x86_64\0");
            sp -= (ulong)platformStr.Length;
            ulong platformAddr = sp;
            _memory.Write(platformAddr, platformStr);

            // Align to 16 bytes
            sp &= ~0xFUL;

            // Auxiliary vector (from bottom up)
            // We'll build from top down, then copy
            var auxv = new (ulong type, ulong value)[]
            {
                (ElfConstants.AT_PHDR, loadResult.ProgramHeaderAddress),
                (ElfConstants.AT_PHENT, loadResult.ProgramHeaderEntrySize),
                (ElfConstants.AT_PHNUM, loadResult.ProgramHeaderCount),
                (ElfConstants.AT_PAGESZ, 4096),
                (ElfConstants.AT_BASE, 0),
                (ElfConstants.AT_FLAGS, 0),
                (ElfConstants.AT_ENTRY, loadResult.EntryPoint),
                (ElfConstants.AT_UID, 1000),
                (ElfConstants.AT_EUID, 1000),
                (ElfConstants.AT_GID, 1000),
                (ElfConstants.AT_EGID, 1000),
                (ElfConstants.AT_CLKTCK, 100),
                (ElfConstants.AT_PLATFORM, platformAddr),
                (ElfConstants.AT_RANDOM, randomAddr),
                (ElfConstants.AT_NULL, 0),
            };

            // Calculate total size needed below current sp
            int totalEntries = 1 + argv.Length + 1 + envp.Length + 1 + (auxv.Length * 2);
            sp -= (ulong)(totalEntries * 8);
            sp &= ~0xFUL; // Align to 16 bytes

            _cpu.RSP = sp;
            ulong ptr = sp;

            // argc
            _memory.WriteUInt64(ptr, (ulong)argv.Length);
            ptr += 8;

            // argv pointers
            for (int i = 0; i < argvPtrs.Length; i++)
            {
                _memory.WriteUInt64(ptr, argvPtrs[i]);
                ptr += 8;
            }
            _memory.WriteUInt64(ptr, 0); // NULL terminator
            ptr += 8;

            // envp pointers
            for (int i = 0; i < envpPtrs.Length; i++)
            {
                _memory.WriteUInt64(ptr, envpPtrs[i]);
                ptr += 8;
            }
            _memory.WriteUInt64(ptr, 0); // NULL terminator
            ptr += 8;

            // Auxiliary vectors
            foreach (var (type, value) in auxv)
            {
                _memory.WriteUInt64(ptr, type);
                ptr += 8;
                _memory.WriteUInt64(ptr, value);
                ptr += 8;
            }

            _logger($"Stack set up: RSP=0x{_cpu.RSP:X16}, argc={argv.Length}");
        }

        private static string[] GetDefaultEnvironment()
        {
            return new[]
            {
                "PATH=/usr/local/bin:/usr/bin:/bin",
                "HOME=/root",
                "TERM=xterm-256color",
                "LANG=en_US.UTF-8",
                "USER=user",
                "SHELL=/bin/sh",
                "COLUMNS=80",
                "LINES=24",
            };
        }
    }
}
