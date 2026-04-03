// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Linux x86_64 syscall dispatch table and handler.
// Syscall numbers are derived from the Linux kernel source:
//   arch/x86/entry/syscalls/syscall_64.tbl
// Argument passing follows the Linux x86_64 ABI:
//   RAX = syscall number
//   RDI, RSI, RDX, R10, R8, R9 = arguments 1-6
//   RAX = return value (negative errno on error)

using System;
using System.Text;
using LinuxBinaryTranslator.Cpu;
using LinuxBinaryTranslator.FileSystem;
using LinuxBinaryTranslator.Memory;

namespace LinuxBinaryTranslator.Syscall
{
    /// <summary>
    /// Linux x86_64 syscall numbers from arch/x86/entry/syscalls/syscall_64.tbl.
    /// </summary>
    public static class SyscallNumber
    {
        public const int SYS_read = 0;
        public const int SYS_write = 1;
        public const int SYS_open = 2;
        public const int SYS_close = 3;
        public const int SYS_stat = 4;
        public const int SYS_fstat = 5;
        public const int SYS_lstat = 6;
        public const int SYS_poll = 7;
        public const int SYS_lseek = 8;
        public const int SYS_mmap = 9;
        public const int SYS_mprotect = 10;
        public const int SYS_munmap = 11;
        public const int SYS_brk = 12;
        public const int SYS_rt_sigaction = 13;
        public const int SYS_rt_sigprocmask = 14;
        public const int SYS_rt_sigreturn = 15;
        public const int SYS_ioctl = 16;
        public const int SYS_pread64 = 17;
        public const int SYS_pwrite64 = 18;
        public const int SYS_readv = 19;
        public const int SYS_writev = 20;
        public const int SYS_access = 21;
        public const int SYS_pipe = 22;
        public const int SYS_select = 23;
        public const int SYS_sched_yield = 24;
        public const int SYS_mremap = 25;
        public const int SYS_msync = 26;
        public const int SYS_mincore = 27;
        public const int SYS_madvise = 28;
        public const int SYS_dup = 32;
        public const int SYS_dup2 = 33;
        public const int SYS_pause = 34;
        public const int SYS_nanosleep = 35;
        public const int SYS_getitimer = 36;
        public const int SYS_alarm = 37;
        public const int SYS_setitimer = 38;
        public const int SYS_getpid = 39;
        public const int SYS_socket = 41;
        public const int SYS_connect = 42;
        public const int SYS_accept = 43;
        public const int SYS_sendto = 44;
        public const int SYS_recvfrom = 45;
        public const int SYS_sendmsg = 46;
        public const int SYS_recvmsg = 47;
        public const int SYS_shutdown = 48;
        public const int SYS_bind = 49;
        public const int SYS_listen = 50;
        public const int SYS_getsockname = 51;
        public const int SYS_getpeername = 52;
        public const int SYS_clone = 56;
        public const int SYS_fork = 57;
        public const int SYS_vfork = 58;
        public const int SYS_execve = 59;
        public const int SYS_exit = 60;
        public const int SYS_wait4 = 61;
        public const int SYS_kill = 62;
        public const int SYS_uname = 63;
        public const int SYS_fcntl = 72;
        public const int SYS_flock = 73;
        public const int SYS_fsync = 74;
        public const int SYS_fdatasync = 75;
        public const int SYS_truncate = 76;
        public const int SYS_ftruncate = 77;
        public const int SYS_getdents = 78;
        public const int SYS_getcwd = 79;
        public const int SYS_chdir = 80;
        public const int SYS_fchdir = 81;
        public const int SYS_rename = 82;
        public const int SYS_mkdir = 83;
        public const int SYS_rmdir = 84;
        public const int SYS_creat = 85;
        public const int SYS_link = 86;
        public const int SYS_unlink = 87;
        public const int SYS_symlink = 88;
        public const int SYS_readlink = 89;
        public const int SYS_chmod = 90;
        public const int SYS_fchmod = 91;
        public const int SYS_chown = 92;
        public const int SYS_fchown = 93;
        public const int SYS_lchown = 94;
        public const int SYS_umask = 95;
        public const int SYS_gettimeofday = 96;
        public const int SYS_getrlimit = 97;
        public const int SYS_getrusage = 98;
        public const int SYS_sysinfo = 99;
        public const int SYS_times = 100;
        public const int SYS_getuid = 102;
        public const int SYS_syslog = 103;
        public const int SYS_getgid = 104;
        public const int SYS_setuid = 105;
        public const int SYS_setgid = 106;
        public const int SYS_geteuid = 107;
        public const int SYS_getegid = 108;
        public const int SYS_setpgid = 109;
        public const int SYS_getppid = 110;
        public const int SYS_getpgrp = 111;
        public const int SYS_setsid = 112;
        public const int SYS_getgroups = 115;
        public const int SYS_setgroups = 116;
        public const int SYS_sigaltstack = 131;
        public const int SYS_arch_prctl = 158;
        public const int SYS_gettid = 186;
        public const int SYS_time = 201;
        public const int SYS_futex = 202;
        public const int SYS_set_tid_address = 218;
        public const int SYS_clock_gettime = 228;
        public const int SYS_clock_getres = 229;
        public const int SYS_clock_nanosleep = 230;
        public const int SYS_exit_group = 231;
        public const int SYS_tgkill = 234;
        public const int SYS_openat = 257;
        public const int SYS_mkdirat = 258;
        public const int SYS_newfstatat = 262;
        public const int SYS_unlinkat = 263;
        public const int SYS_renameat = 264;
        public const int SYS_faccessat = 269;
        public const int SYS_pselect6 = 270;
        public const int SYS_set_robust_list = 273;
        public const int SYS_get_robust_list = 274;
        public const int SYS_pipe2 = 293;
        public const int SYS_dup3 = 292;
        public const int SYS_prlimit64 = 302;
        public const int SYS_getrandom = 318;
        public const int SYS_getdents64 = 217;
        public const int SYS_readlinkat = 267;
        public const int SYS_getrusage = 98;
        public const int SYS_times = 100;
        public const int SYS_madvise = 28;
        public const int SYS_poll = 7;
        public const int SYS_select = 23;
        public const int SYS_futex = 202;
        public const int SYS_clock_nanosleep = 230;
        public const int SYS_flock = 73;
        public const int SYS_fsync = 74;
        public const int SYS_fdatasync = 75;
        public const int SYS_truncate = 76;
        public const int SYS_ftruncate = 77;
        public const int SYS_readlink = 89;
        public const int SYS_mkdir = 83;
        public const int SYS_rmdir = 84;
        public const int SYS_unlink = 87;
        public const int SYS_rename = 82;
        public const int SYS_creat = 85;
        public const int SYS_link = 86;
        public const int SYS_symlink = 88;
        public const int SYS_chmod = 90;
        public const int SYS_fchmod = 91;
        public const int SYS_chown = 92;
        public const int SYS_fchown = 93;
        public const int SYS_lchown = 94;
        public const int SYS_getgroups = 115;
        public const int SYS_setgroups = 116;
        public const int SYS_syslog = 103;
        public const int SYS_getitimer = 36;
        public const int SYS_alarm = 37;
        public const int SYS_setitimer = 38;
        public const int SYS_pause = 34;
        public const int SYS_rt_sigreturn = 15;
        public const int SYS_pread64 = 17;
        public const int SYS_pwrite64 = 18;
        public const int SYS_mincore = 27;
        public const int SYS_mremap = 25;
        public const int SYS_msync = 26;
        public const int SYS_mkdirat = 258;
        public const int SYS_unlinkat = 263;
        public const int SYS_renameat = 264;
        public const int SYS_fchdir = 81;
    }

    /// <summary>
    /// arch_prctl sub-commands from kernel arch/x86/include/uapi/asm/prctl.h.
    /// </summary>
    public static class ArchPrctlCmd
    {
        public const int ARCH_SET_GS = 0x1001;
        public const int ARCH_SET_FS = 0x1002;
        public const int ARCH_GET_FS = 0x1003;
        public const int ARCH_GET_GS = 0x1004;
    }

    /// <summary>
    /// Handles Linux syscalls by dispatching to the appropriate implementation.
    /// All syscall arguments are read from the CpuState per the x86_64 Linux ABI.
    /// </summary>
    public sealed class SyscallHandler
    {
        private readonly VirtualMemoryManager _memory;
        private readonly VirtualFileSystem _vfs;
        private readonly Action<string> _logger;

        // Process identity (emulated)
        private readonly int _pid = 1000;
        private readonly int _uid = 1000;
        private readonly int _gid = 1000;
        private int _umask = 0x0022; // 022 octal

        // Working directory
        private string _cwd = "/";

        // Signal handling (stub — just track registered handlers)
        private readonly long[] _signalHandlers = new long[65];

        public SyscallHandler(VirtualMemoryManager memory, VirtualFileSystem vfs, Action<string>? logger = null)
        {
            _memory = memory;
            _vfs = vfs;
            _logger = logger ?? (_ => { });
        }

        /// <summary>
        /// Dispatch a syscall. Called by the block translator when a SYSCALL
        /// instruction is executed.
        /// </summary>
        public long Dispatch(CpuState state, VirtualMemoryManager memory)
        {
            long syscallNum = (long)state.RAX;
            ulong arg1 = state.RDI;
            ulong arg2 = state.RSI;
            ulong arg3 = state.RDX;
            ulong arg4 = state.R10;
            ulong arg5 = state.R8;
            ulong arg6 = state.R9;

            long result;

            try
            {
                result = (int)syscallNum switch
                {
                    SyscallNumber.SYS_read => SysRead((int)arg1, arg2, arg3),
                    SyscallNumber.SYS_write => SysWrite((int)arg1, arg2, arg3),
                    SyscallNumber.SYS_open => SysOpen(arg1, (int)arg2, (int)arg3),
                    SyscallNumber.SYS_close => SysClose((int)arg1),
                    SyscallNumber.SYS_fstat => SysFstat((int)arg1, arg2),
                    SyscallNumber.SYS_stat => SysStat(arg1, arg2),
                    SyscallNumber.SYS_lstat => SysStat(arg1, arg2), // lstat = stat for our VFS
                    SyscallNumber.SYS_lseek => SysLseek((int)arg1, (long)arg2, (int)arg3),
                    SyscallNumber.SYS_mmap => SysMmap(arg1, arg2, (int)arg3, (int)arg4, (int)arg5, (long)arg6),
                    SyscallNumber.SYS_mprotect => SysMprotect(arg1, arg2, (int)arg3),
                    SyscallNumber.SYS_munmap => SysMunmap(arg1, arg2),
                    SyscallNumber.SYS_brk => SysBrk(arg1),
                    SyscallNumber.SYS_ioctl => SysIoctl((int)arg1, arg2, arg3),
                    SyscallNumber.SYS_writev => SysWritev((int)arg1, arg2, (int)arg3),
                    SyscallNumber.SYS_readv => SysReadv((int)arg1, arg2, (int)arg3),
                    SyscallNumber.SYS_access => SysAccess(arg1, (int)arg2),
                    SyscallNumber.SYS_dup => SysDup((int)arg1),
                    SyscallNumber.SYS_dup2 => SysDup2((int)arg1, (int)arg2),
                    SyscallNumber.SYS_dup3 => SysDup3((int)arg1, (int)arg2, (int)arg3),
                    SyscallNumber.SYS_pipe => SysPipe(arg1),
                    SyscallNumber.SYS_pipe2 => SysPipe2(arg1, (int)arg2),
                    SyscallNumber.SYS_nanosleep => SysNanosleep(arg1, arg2),
                    SyscallNumber.SYS_getpid => _pid,
                    SyscallNumber.SYS_getppid => 1,
                    SyscallNumber.SYS_gettid => _pid,
                    SyscallNumber.SYS_getuid => _uid,
                    SyscallNumber.SYS_geteuid => _uid,
                    SyscallNumber.SYS_getgid => _gid,
                    SyscallNumber.SYS_getegid => _gid,
                    SyscallNumber.SYS_getpgrp => _pid,
                    SyscallNumber.SYS_setsid => _pid,
                    SyscallNumber.SYS_setpgid => 0,
                    SyscallNumber.SYS_setuid => 0,
                    SyscallNumber.SYS_setgid => 0,
                    SyscallNumber.SYS_umask => SysUmask((int)arg1),
                    SyscallNumber.SYS_uname => SysUname(arg1),
                    SyscallNumber.SYS_fcntl => SysFcntl((int)arg1, (int)arg2, arg3),
                    SyscallNumber.SYS_getcwd => SysGetcwd(arg1, arg2),
                    SyscallNumber.SYS_chdir => SysChdir(arg1),
                    SyscallNumber.SYS_openat => SysOpenat((int)arg1, arg2, (int)arg3, (int)arg4),
                    SyscallNumber.SYS_newfstatat => SysNewfstatat((int)arg1, arg2, arg3, (int)arg4),
                    SyscallNumber.SYS_faccessat => SysFaccessat((int)arg1, arg2, (int)arg3),
                    SyscallNumber.SYS_rt_sigaction => SysRtSigaction((int)arg1, arg2, arg3, arg4),
                    SyscallNumber.SYS_rt_sigprocmask => 0, // Stub: signal mask management
                    SyscallNumber.SYS_sigaltstack => 0, // Stub
                    SyscallNumber.SYS_arch_prctl => SysArchPrctl(state, (int)arg1, arg2),
                    SyscallNumber.SYS_set_tid_address => _pid,
                    SyscallNumber.SYS_set_robust_list => 0,
                    SyscallNumber.SYS_get_robust_list => -Errno.ENOSYS,
                    SyscallNumber.SYS_gettimeofday => SysGettimeofday(arg1, arg2),
                    SyscallNumber.SYS_clock_gettime => SysClockGettime((int)arg1, arg2),
                    SyscallNumber.SYS_clock_getres => SysClockGetres((int)arg1, arg2),
                    SyscallNumber.SYS_time => SysTime(arg1),
                    SyscallNumber.SYS_getrandom => SysGetrandom(arg1, arg2, (uint)arg3),
                    SyscallNumber.SYS_sysinfo => SysSysinfo(arg1),
                    SyscallNumber.SYS_getrlimit => SysGetrlimit((int)arg1, arg2),
                    SyscallNumber.SYS_prlimit64 => SysPrlimit64((int)arg1, (int)arg2, arg3, arg4),
                    SyscallNumber.SYS_sched_yield => 0,
                    SyscallNumber.SYS_exit => SysExit(state, (int)arg1),
                    SyscallNumber.SYS_exit_group => SysExit(state, (int)arg1),
                    SyscallNumber.SYS_kill => 0, // Stub
                    SyscallNumber.SYS_tgkill => 0, // Stub
                    SyscallNumber.SYS_getdents64 => SysGetdents64((int)arg1, arg2, (int)arg3),
                    SyscallNumber.SYS_readlink => SysReadlink(arg1, arg2, arg3),
                    SyscallNumber.SYS_readlinkat => SysReadlinkat((int)arg1, arg2, arg3, arg4),
                    SyscallNumber.SYS_poll => SysPoll(arg1, (int)arg2, (int)arg3),
                    SyscallNumber.SYS_select => 0, // Stub: simple select
                    SyscallNumber.SYS_futex => SysFutex(arg1, (int)arg2, (uint)arg3, arg4, arg5, (uint)arg6),
                    SyscallNumber.SYS_madvise => 0, // Advisory only — always succeed
                    SyscallNumber.SYS_mincore => -Errno.ENOSYS,
                    SyscallNumber.SYS_msync => 0,
                    SyscallNumber.SYS_mremap => -Errno.ENOSYS,
                    SyscallNumber.SYS_getrusage => SysGetrusage((int)arg1, arg2),
                    SyscallNumber.SYS_times => SysTimes(arg1),
                    SyscallNumber.SYS_clock_nanosleep => SysClockNanosleep((int)arg1, (int)arg2, arg3, arg4),
                    SyscallNumber.SYS_flock => 0, // File locking — always succeed
                    SyscallNumber.SYS_fsync => 0,
                    SyscallNumber.SYS_fdatasync => 0,
                    SyscallNumber.SYS_truncate => -Errno.EROFS,
                    SyscallNumber.SYS_ftruncate => -Errno.EROFS,
                    SyscallNumber.SYS_mkdir => -Errno.EROFS,
                    SyscallNumber.SYS_rmdir => -Errno.EROFS,
                    SyscallNumber.SYS_unlink => -Errno.EROFS,
                    SyscallNumber.SYS_rename => -Errno.EROFS,
                    SyscallNumber.SYS_creat => SysOpen(arg1, OpenFlags.O_CREAT | OpenFlags.O_WRONLY | OpenFlags.O_TRUNC, (int)arg2),
                    SyscallNumber.SYS_link => -Errno.EROFS,
                    SyscallNumber.SYS_symlink => -Errno.EROFS,
                    SyscallNumber.SYS_chmod => 0,
                    SyscallNumber.SYS_fchmod => 0,
                    SyscallNumber.SYS_chown => 0,
                    SyscallNumber.SYS_fchown => 0,
                    SyscallNumber.SYS_lchown => 0,
                    SyscallNumber.SYS_getgroups => 0, // No supplementary groups
                    SyscallNumber.SYS_setgroups => 0,
                    SyscallNumber.SYS_syslog => -Errno.EPERM,
                    SyscallNumber.SYS_getitimer => -Errno.ENOSYS,
                    SyscallNumber.SYS_alarm => 0, // Alarm — stub
                    SyscallNumber.SYS_setitimer => -Errno.ENOSYS,
                    SyscallNumber.SYS_pause => SysPause(),
                    SyscallNumber.SYS_pread64 => SysPread64((int)arg1, arg2, arg3, (long)arg4),
                    SyscallNumber.SYS_pwrite64 => SysPwrite64((int)arg1, arg2, arg3, (long)arg4),
                    SyscallNumber.SYS_mkdirat => -Errno.EROFS,
                    SyscallNumber.SYS_unlinkat => -Errno.EROFS,
                    SyscallNumber.SYS_renameat => -Errno.EROFS,
                    SyscallNumber.SYS_fchdir => 0,
                    _ => HandleUnimplemented((int)syscallNum),
                };
            }
            catch (Exception ex)
            {
                _logger($"Syscall {syscallNum} threw: {ex.Message}");
                result = -Errno.EFAULT;
            }

            return result;
        }

        // === File I/O syscalls ===

        private long SysRead(int fd, ulong bufAddr, ulong count)
        {
            if (count == 0) return 0;
            byte[] data = _vfs.Read(fd, (int)Math.Min(count, int.MaxValue));
            if (data.Length < 0) return data.Length; // Error
            _memory.Write(bufAddr, data);
            return data.Length;
        }

        private long SysWrite(int fd, ulong bufAddr, ulong count)
        {
            if (count == 0) return 0;
            byte[] data = _memory.Read(bufAddr, count);
            return _vfs.Write(fd, data);
        }

        private long SysOpen(ulong pathAddr, int flags, int mode)
        {
            string path = ReadString(pathAddr);
            return _vfs.Open(ResolvePath(path), flags, mode);
        }

        private long SysClose(int fd)
        {
            return _vfs.Close(fd);
        }

        private long SysFstat(int fd, ulong statBuf)
        {
            var stat = _vfs.Fstat(fd);
            if (stat == null) return -Errno.EBADF;
            WriteStatBuf(statBuf, stat);
            return 0;
        }

        private long SysStat(ulong pathAddr, ulong statBuf)
        {
            string path = ReadString(pathAddr);
            var stat = _vfs.Stat(ResolvePath(path));
            if (stat == null) return -Errno.ENOENT;
            WriteStatBuf(statBuf, stat);
            return 0;
        }

        private long SysLseek(int fd, long offset, int whence)
        {
            return _vfs.Lseek(fd, offset, whence);
        }

        private long SysWritev(int fd, ulong iovAddr, int iovcnt)
        {
            long totalWritten = 0;
            for (int i = 0; i < iovcnt; i++)
            {
                ulong entryAddr = iovAddr + (ulong)(i * 16); // struct iovec is 16 bytes on x86_64
                ulong iov_base = _memory.ReadUInt64(entryAddr);
                ulong iov_len = _memory.ReadUInt64(entryAddr + 8);
                if (iov_len > 0)
                {
                    byte[] data = _memory.Read(iov_base, iov_len);
                    long written = _vfs.Write(fd, data);
                    if (written < 0) return written;
                    totalWritten += written;
                }
            }
            return totalWritten;
        }

        private long SysReadv(int fd, ulong iovAddr, int iovcnt)
        {
            long totalRead = 0;
            for (int i = 0; i < iovcnt; i++)
            {
                ulong entryAddr = iovAddr + (ulong)(i * 16);
                ulong iov_base = _memory.ReadUInt64(entryAddr);
                ulong iov_len = _memory.ReadUInt64(entryAddr + 8);
                if (iov_len > 0)
                {
                    byte[] data = _vfs.Read(fd, (int)Math.Min(iov_len, int.MaxValue));
                    if (data.Length == 0) break;
                    _memory.Write(iov_base, data);
                    totalRead += data.Length;
                }
            }
            return totalRead;
        }

        private long SysAccess(ulong pathAddr, int mode)
        {
            string path = ReadString(pathAddr);
            return _vfs.Access(ResolvePath(path), mode) ? 0 : -Errno.ENOENT;
        }

        private long SysDup(int oldfd) => _vfs.Dup(oldfd);
        private long SysDup2(int oldfd, int newfd) => _vfs.Dup2(oldfd, newfd);
        private long SysDup3(int oldfd, int newfd, int flags) => _vfs.Dup2(oldfd, newfd);

        private long SysPipe(ulong fdsAddr)
        {
            int readFd, writeFd;
            int result = _vfs.Pipe(out readFd, out writeFd);
            if (result < 0) return result;
            _memory.WriteUInt32(fdsAddr, (uint)readFd);
            _memory.WriteUInt32(fdsAddr + 4, (uint)writeFd);
            return 0;
        }

        private long SysPipe2(ulong fdsAddr, int flags) => SysPipe(fdsAddr);

        private long SysFcntl(int fd, int cmd, ulong arg)
        {
            switch (cmd)
            {
                case FcntlCmd.F_GETFD: return 0;
                case FcntlCmd.F_SETFD: return 0;
                case FcntlCmd.F_GETFL: return _vfs.GetFlags(fd);
                case FcntlCmd.F_SETFL: return 0;
                case FcntlCmd.F_DUPFD:
                case FcntlCmd.F_DUPFD_CLOEXEC:
                    return _vfs.Dup(fd);
                default:
                    return -Errno.EINVAL;
            }
        }

        private long SysIoctl(int fd, ulong request, ulong arg)
        {
            // Terminal ioctl handling
            switch ((uint)request)
            {
                case TermiosConstants.TCGETS:
                    // Return default termios
                    WriteDefaultTermios(arg);
                    return 0;
                case TermiosConstants.TIOCGWINSZ:
                    // Return terminal window size (80x24)
                    _memory.WriteUInt16(arg, 24);      // ws_row
                    _memory.WriteUInt16(arg + 2, 80);  // ws_col
                    _memory.WriteUInt16(arg + 4, 0);   // ws_xpixel
                    _memory.WriteUInt16(arg + 6, 0);   // ws_ypixel
                    return 0;
                case TermiosConstants.TIOCGPGRP:
                    _memory.WriteUInt32(arg, (uint)_pid);
                    return 0;
                case TermiosConstants.TCSETS:
                case TermiosConstants.TCSETSW:
                case TermiosConstants.TCSETSF:
                case TermiosConstants.TIOCSPGRP:
                case TermiosConstants.TIOCSWINSZ:
                    return 0; // Silently accept
                default:
                    return -Errno.ENOTTY;
            }
        }

        // === Memory syscalls ===

        private long SysMmap(ulong addr, ulong length, int prot, int flags, int fd, long offset)
        {
            var protection = MemoryProtection.None;
            if ((prot & MmapFlags.PROT_READ) != 0) protection |= MemoryProtection.Read;
            if ((prot & MmapFlags.PROT_WRITE) != 0) protection |= MemoryProtection.Write;
            if ((prot & MmapFlags.PROT_EXEC) != 0) protection |= MemoryProtection.Execute;

            bool isAnon = (flags & MmapFlags.MAP_ANONYMOUS) != 0;
            bool isFixed = (flags & MmapFlags.MAP_FIXED) != 0;

            ulong requestAddr = isFixed ? addr : 0;
            ulong result = _memory.Map(requestAddr, length, protection);

            if (result == unchecked((ulong)-1L))
                return -Errno.ENOMEM;

            // For file-backed mappings, read data from the VFS
            if (!isAnon && fd >= 0)
            {
                long savedPos = _vfs.Lseek(fd, 0, SeekWhence.SEEK_CUR);
                _vfs.Lseek(fd, offset, SeekWhence.SEEK_SET);
                byte[] data = _vfs.Read(fd, (int)Math.Min(length, int.MaxValue));
                _memory.Write(result, data);
                _vfs.Lseek(fd, savedPos, SeekWhence.SEEK_SET);
            }

            return (long)result;
        }

        private long SysMprotect(ulong addr, ulong len, int prot)
        {
            var protection = MemoryProtection.None;
            if ((prot & MmapFlags.PROT_READ) != 0) protection |= MemoryProtection.Read;
            if ((prot & MmapFlags.PROT_WRITE) != 0) protection |= MemoryProtection.Write;
            if ((prot & MmapFlags.PROT_EXEC) != 0) protection |= MemoryProtection.Execute;
            return _memory.Protect(addr, len, protection);
        }

        private long SysMunmap(ulong addr, ulong len)
        {
            return _memory.Unmap(addr, len);
        }

        private long SysBrk(ulong addr)
        {
            return (long)_memory.Brk(addr);
        }

        // === Process/identity syscalls ===

        private long SysExit(CpuState state, int exitCode)
        {
            state.Halted = true;
            state.ExitCode = exitCode;
            return 0;
        }

        private long SysUname(ulong bufAddr)
        {
            // struct utsname: 5 fields of 65 bytes each (from kernel utsname.h)
            const int fieldLen = 65;
            WriteStringFixed(bufAddr, "Linux", fieldLen);
            WriteStringFixed(bufAddr + fieldLen, "localhost", fieldLen);
            WriteStringFixed(bufAddr + fieldLen * 2, "6.1.0-uwp-translator", fieldLen);
            WriteStringFixed(bufAddr + fieldLen * 3, "#1 SMP", fieldLen);
            WriteStringFixed(bufAddr + fieldLen * 4, "x86_64", fieldLen);
            // domainname
            WriteStringFixed(bufAddr + fieldLen * 5, "(none)", fieldLen);
            return 0;
        }

        private long SysArchPrctl(CpuState state, int code, ulong addr)
        {
            switch (code)
            {
                case ArchPrctlCmd.ARCH_SET_FS:
                    state.FSBase = addr;
                    return 0;
                case ArchPrctlCmd.ARCH_GET_FS:
                    _memory.WriteUInt64(addr, state.FSBase);
                    return 0;
                case ArchPrctlCmd.ARCH_SET_GS:
                    state.GSBase = addr;
                    return 0;
                case ArchPrctlCmd.ARCH_GET_GS:
                    _memory.WriteUInt64(addr, state.GSBase);
                    return 0;
                default:
                    return -Errno.EINVAL;
            }
        }

        private long SysUmask(int mask)
        {
            int old = _umask;
            _umask = mask & 0x1FF;
            return old;
        }

        // === Filesystem navigation ===

        private long SysGetcwd(ulong bufAddr, ulong size)
        {
            byte[] cwdBytes = Encoding.UTF8.GetBytes(_cwd + "\0");
            if ((ulong)cwdBytes.Length > size)
                return -Errno.ERANGE;
            _memory.Write(bufAddr, cwdBytes);
            return cwdBytes.Length;
        }

        private long SysChdir(ulong pathAddr)
        {
            string path = ReadString(pathAddr);
            _cwd = ResolvePath(path);
            return 0;
        }

        private long SysOpenat(int dirfd, ulong pathAddr, int flags, int mode)
        {
            string path = ReadString(pathAddr);
            if (path.Length > 0 && path[0] != '/' && dirfd == OpenFlags.AT_FDCWD)
                path = _cwd + "/" + path;
            else if (path.Length > 0 && path[0] != '/')
                path = "/" + path; // Relative to dirfd (simplified)
            return _vfs.Open(path, flags, mode);
        }

        private long SysNewfstatat(int dirfd, ulong pathAddr, ulong statBuf, int flags)
        {
            string path = ReadString(pathAddr);
            if (path.Length == 0)
                return SysFstat(dirfd, statBuf);
            if (path[0] != '/' && dirfd == OpenFlags.AT_FDCWD)
                path = _cwd + "/" + path;
            var stat = _vfs.Stat(path);
            if (stat == null) return -Errno.ENOENT;
            WriteStatBuf(statBuf, stat);
            return 0;
        }

        private long SysFaccessat(int dirfd, ulong pathAddr, int mode)
        {
            string path = ReadString(pathAddr);
            if (path.Length > 0 && path[0] != '/' && dirfd == OpenFlags.AT_FDCWD)
                path = _cwd + "/" + path;
            return _vfs.Access(path, mode) ? 0 : -Errno.ENOENT;
        }

        // === Signal syscalls ===

        private long SysRtSigaction(int sig, ulong act, ulong oldact, ulong sigsetsize)
        {
            if (sig < 1 || sig > 64) return -Errno.EINVAL;
            if (sig == Signals.SIGKILL || sig == Signals.SIGSTOP) return -Errno.EINVAL;

            if (oldact != 0)
            {
                _memory.WriteUInt64(oldact, (ulong)_signalHandlers[sig]);
                _memory.WriteUInt64(oldact + 8, 0); // sa_flags
                _memory.WriteUInt64(oldact + 16, 0); // sa_restorer
                _memory.WriteUInt64(oldact + 24, 0); // sa_mask
            }

            if (act != 0)
            {
                _signalHandlers[sig] = (long)_memory.ReadUInt64(act);
            }

            return 0;
        }

        // === Time syscalls ===

        private long SysGettimeofday(ulong tvAddr, ulong tzAddr)
        {
            if (tvAddr != 0)
            {
                var now = DateTimeOffset.UtcNow;
                long seconds = now.ToUnixTimeSeconds();
                long microseconds = (now.ToUnixTimeMilliseconds() % 1000) * 1000;
                _memory.WriteUInt64(tvAddr, (ulong)seconds);
                _memory.WriteUInt64(tvAddr + 8, (ulong)microseconds);
            }
            if (tzAddr != 0)
            {
                _memory.WriteUInt32(tzAddr, 0); // tz_minuteswest
                _memory.WriteUInt32(tzAddr + 4, 0); // tz_dsttime
            }
            return 0;
        }

        private long SysClockGettime(int clockId, ulong tpAddr)
        {
            var now = DateTimeOffset.UtcNow;
            long seconds = now.ToUnixTimeSeconds();
            long nanoseconds = (now.ToUnixTimeMilliseconds() % 1000) * 1000000;
            _memory.WriteUInt64(tpAddr, (ulong)seconds);
            _memory.WriteUInt64(tpAddr + 8, (ulong)nanoseconds);
            return 0;
        }

        private long SysClockGetres(int clockId, ulong resAddr)
        {
            if (resAddr != 0)
            {
                _memory.WriteUInt64(resAddr, 0);
                _memory.WriteUInt64(resAddr + 8, 1000000); // 1ms resolution
            }
            return 0;
        }

        private long SysTime(ulong tloc)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (tloc != 0)
                _memory.WriteUInt64(tloc, (ulong)now);
            return now;
        }

        private long SysNanosleep(ulong reqAddr, ulong remAddr)
        {
            long sec = (long)_memory.ReadUInt64(reqAddr);
            long nsec = (long)_memory.ReadUInt64(reqAddr + 8);
            int ms = (int)(sec * 1000 + nsec / 1000000);
            if (ms > 0)
                System.Threading.Tasks.Task.Delay(ms).Wait();
            if (remAddr != 0)
            {
                _memory.WriteUInt64(remAddr, 0);
                _memory.WriteUInt64(remAddr + 8, 0);
            }
            return 0;
        }

        // === System info ===

        private long SysGetrandom(ulong buf, ulong count, uint flags)
        {
            var rand = new Random();
            byte[] data = new byte[Math.Min(count, 256)];
            rand.NextBytes(data);
            _memory.Write(buf, data);
            return data.Length;
        }

        private long SysSysinfo(ulong infoAddr)
        {
            // struct sysinfo - simplified
            _memory.WriteUInt64(infoAddr, (ulong)Environment.TickCount / 1000); // uptime
            // loads[3]
            _memory.WriteUInt64(infoAddr + 8, 0);
            _memory.WriteUInt64(infoAddr + 16, 0);
            _memory.WriteUInt64(infoAddr + 24, 0);
            // totalram, freeram
            _memory.WriteUInt64(infoAddr + 32, 512UL * 1024 * 1024);
            _memory.WriteUInt64(infoAddr + 40, 256UL * 1024 * 1024);
            return 0;
        }

        private long SysGetrlimit(int resource, ulong rlimAddr)
        {
            // Return generous limits
            ulong soft = 0x7FFFFFFFUL;
            ulong hard = 0x7FFFFFFFUL;
            _memory.WriteUInt64(rlimAddr, soft);
            _memory.WriteUInt64(rlimAddr + 8, hard);
            return 0;
        }

        private long SysPrlimit64(int pid, int resource, ulong newLimit, ulong oldLimit)
        {
            if (oldLimit != 0)
            {
                _memory.WriteUInt64(oldLimit, 0x7FFFFFFFUL);
                _memory.WriteUInt64(oldLimit + 8, 0x7FFFFFFFUL);
            }
            return 0;
        }

        // === Helpers ===

        // === New syscall implementations ===

        private long SysGetdents64(int fd, ulong bufAddr, int count)
        {
            // getdents64 returns directory entries. For our simple VFS,
            // return "." and ".." entries for directories, or ENOTDIR for files
            if (fd < 0) return -Errno.EBADF;
            // Simplified: return 0 (end of directory) since our VFS
            // doesn't support real directory listing
            return 0;
        }

        private long SysReadlink(ulong pathAddr, ulong bufAddr, ulong bufSize)
        {
            string path = ReadString(pathAddr);
            return DoReadlink(ResolvePath(path), bufAddr, bufSize);
        }

        private long SysReadlinkat(int dirfd, ulong pathAddr, ulong bufAddr, ulong bufSize)
        {
            string path = ReadString(pathAddr);
            if (path.Length > 0 && path[0] != '/' && dirfd == OpenFlags.AT_FDCWD)
                path = _cwd + "/" + path;
            return DoReadlink(path, bufAddr, bufSize);
        }

        private long DoReadlink(string path, ulong bufAddr, ulong bufSize)
        {
            // Handle /proc/self/exe — programs often readlink this
            if (path == "/proc/self/exe")
            {
                string target = "/usr/bin/program";
                byte[] data = Encoding.UTF8.GetBytes(target);
                int len = (int)Math.Min((ulong)data.Length, bufSize);
                _memory.Write(bufAddr, data.AsSpan(0, len).ToArray());
                return len;
            }
            // Handle /proc/self/fd/N
            if (path.StartsWith("/proc/self/fd/"))
            {
                string target = "/dev/fd/" + path.Substring(14);
                byte[] data = Encoding.UTF8.GetBytes(target);
                int len = (int)Math.Min((ulong)data.Length, bufSize);
                _memory.Write(bufAddr, data.AsSpan(0, len).ToArray());
                return len;
            }
            return -Errno.EINVAL;
        }

        private long SysPoll(ulong fdsAddr, int nfds, int timeout)
        {
            // poll() — simplified implementation for stdin readiness
            // struct pollfd { int fd; short events; short revents; }
            int ready = 0;
            for (int i = 0; i < nfds; i++)
            {
                ulong entry = fdsAddr + (ulong)(i * 8);
                int fd = (int)_memory.ReadUInt32(entry);
                short events = (short)_memory.ReadUInt16(entry + 4);
                short revents = 0;

                // Check if fd is valid
                if (_vfs.Fstat(fd) != null)
                {
                    // POLLIN = 1, POLLOUT = 4
                    if ((events & 1) != 0) revents |= 1;    // Readable
                    if ((events & 4) != 0) revents |= 4;    // Writable
                    if (revents != 0) ready++;
                }
                else
                {
                    revents = 0x20; // POLLNVAL
                }

                _memory.WriteUInt16(entry + 6, (ushort)revents);
            }

            if (timeout > 0)
                System.Threading.Tasks.Task.Delay(Math.Min(timeout, 100)).Wait();

            return ready;
        }

        private long SysFutex(ulong uaddr, int futexOp, uint val, ulong timeout, ulong uaddr2, uint val3)
        {
            // futex() — basic implementation for single-threaded use
            const int FUTEX_WAIT = 0;
            const int FUTEX_WAKE = 1;
            const int FUTEX_PRIVATE_FLAG = 128;

            int op = futexOp & ~FUTEX_PRIVATE_FLAG;

            switch (op)
            {
                case FUTEX_WAIT:
                {
                    // Check if *uaddr == val; if so, sleep; if not, return EAGAIN
                    uint current = _memory.ReadUInt32(uaddr);
                    if (current != val)
                        return -Errno.EAGAIN;
                    // For single-threaded: just yield and return
                    System.Threading.Thread.Yield();
                    return 0;
                }
                case FUTEX_WAKE:
                    // Wake up to 'val' waiters — in single-threaded mode, return 0
                    return 0;
                default:
                    return -Errno.ENOSYS;
            }
        }

        private long SysGetrusage(int who, ulong usageAddr)
        {
            // struct rusage — zero-fill for minimal implementation
            // Total size: 144 bytes on x86_64
            _memory.Zero(usageAddr, 144);
            return 0;
        }

        private long SysTimes(ulong bufAddr)
        {
            // struct tms { clock_t tms_utime, tms_stime, tms_cutime, tms_cstime; }
            if (bufAddr != 0)
            {
                long ticks = Environment.TickCount;
                _memory.WriteUInt64(bufAddr, (ulong)ticks);      // tms_utime
                _memory.WriteUInt64(bufAddr + 8, 0);             // tms_stime
                _memory.WriteUInt64(bufAddr + 16, 0);            // tms_cutime
                _memory.WriteUInt64(bufAddr + 24, 0);            // tms_cstime
            }
            return Environment.TickCount;
        }

        private long SysClockNanosleep(int clockId, int flags, ulong reqAddr, ulong remAddr)
        {
            long sec = (long)_memory.ReadUInt64(reqAddr);
            long nsec = (long)_memory.ReadUInt64(reqAddr + 8);
            int ms = (int)(sec * 1000 + nsec / 1000000);
            if (ms > 0)
                System.Threading.Tasks.Task.Delay(ms).Wait();
            if (remAddr != 0)
            {
                _memory.WriteUInt64(remAddr, 0);
                _memory.WriteUInt64(remAddr + 8, 0);
            }
            return 0;
        }

        private long SysPause()
        {
            // pause() — wait for signal delivery (simplified: sleep briefly then return EINTR)
            System.Threading.Tasks.Task.Delay(100).Wait();
            return -Errno.EINTR;
        }

        private long SysPread64(int fd, ulong bufAddr, ulong count, long offset)
        {
            // pread64 — read at offset without changing file position
            long savedPos = _vfs.Lseek(fd, 0, SeekWhence.SEEK_CUR);
            _vfs.Lseek(fd, offset, SeekWhence.SEEK_SET);
            byte[] data = _vfs.Read(fd, (int)Math.Min(count, int.MaxValue));
            _vfs.Lseek(fd, savedPos, SeekWhence.SEEK_SET);
            if (data.Length > 0)
                _memory.Write(bufAddr, data);
            return data.Length;
        }

        private long SysPwrite64(int fd, ulong bufAddr, ulong count, long offset)
        {
            // pwrite64 — write at offset without changing file position
            long savedPos = _vfs.Lseek(fd, 0, SeekWhence.SEEK_CUR);
            _vfs.Lseek(fd, offset, SeekWhence.SEEK_SET);
            byte[] data = _memory.Read(bufAddr, count);
            int written = _vfs.Write(fd, data);
            _vfs.Lseek(fd, savedPos, SeekWhence.SEEK_SET);
            return written;
        }

        private long HandleUnimplemented(int num)
        {
            _logger($"Unimplemented syscall: {num}");
            return -Errno.ENOSYS;
        }

        private string ReadString(ulong address)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 4096; i++)
            {
                byte b = _memory.ReadByte(address + (ulong)i);
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private void WriteStringFixed(ulong address, string value, int fieldLen)
        {
            byte[] bytes = new byte[fieldLen];
            byte[] encoded = Encoding.UTF8.GetBytes(value);
            Array.Copy(encoded, bytes, Math.Min(encoded.Length, fieldLen - 1));
            _memory.Write(address, bytes);
        }

        private string ResolvePath(string path)
        {
            if (path.Length == 0) return _cwd;
            if (path[0] == '/') return path;
            if (_cwd == "/") return "/" + path;
            return _cwd + "/" + path;
        }

        private void WriteStatBuf(ulong addr, VfsStatResult stat)
        {
            // struct stat layout for x86_64 (from kernel include/uapi/asm-generic/stat.h)
            _memory.WriteUInt64(addr + 0, stat.Dev);          // st_dev
            _memory.WriteUInt64(addr + 8, stat.Ino);          // st_ino
            _memory.WriteUInt64(addr + 16, stat.Nlink);       // st_nlink
            _memory.WriteUInt32(addr + 24, stat.Mode);        // st_mode
            _memory.WriteUInt32(addr + 28, stat.Uid);         // st_uid
            _memory.WriteUInt32(addr + 32, stat.Gid);         // st_gid
            _memory.WriteUInt32(addr + 36, 0);                // padding
            _memory.WriteUInt64(addr + 40, stat.Rdev);        // st_rdev
            _memory.WriteUInt64(addr + 48, (ulong)stat.Size); // st_size
            _memory.WriteUInt64(addr + 56, 4096);             // st_blksize
            _memory.WriteUInt64(addr + 64, (ulong)(stat.Size / 512)); // st_blocks
            // st_atime, st_mtime, st_ctime (each 16 bytes: sec + nsec)
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _memory.WriteUInt64(addr + 72, (ulong)now);
            _memory.WriteUInt64(addr + 80, 0); // nsec
            _memory.WriteUInt64(addr + 88, (ulong)now);
            _memory.WriteUInt64(addr + 96, 0);
            _memory.WriteUInt64(addr + 104, (ulong)now);
            _memory.WriteUInt64(addr + 112, 0);
        }

        private void WriteDefaultTermios(ulong addr)
        {
            // Default termios: canonical mode with echo
            _memory.WriteUInt32(addr, TermiosConstants.ICRNL | TermiosConstants.IXON); // c_iflag
            _memory.WriteUInt32(addr + 4, TermiosConstants.OPOST | TermiosConstants.ONLCR); // c_oflag
            _memory.WriteUInt32(addr + 8, 0); // c_cflag
            _memory.WriteUInt32(addr + 12, TermiosConstants.ECHO | TermiosConstants.ICANON | TermiosConstants.ISIG | TermiosConstants.IEXTEN); // c_lflag
            _memory.WriteByte(addr + 16, 0); // c_line
            // c_cc array (19 bytes)
            _memory.Zero(addr + 17, TermiosConstants.NCCS);
        }
    }
}
