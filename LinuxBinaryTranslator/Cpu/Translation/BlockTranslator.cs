// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Native block translator: translates x86_64 basic blocks into C# delegates.
// This is the core of the "native translation" approach — each basic block of
// machine code is decoded once and converted into a TranslatedBlock delegate
// that directly manipulates CpuState and VirtualMemoryManager without
// per-instruction decode overhead at runtime.

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LinuxBinaryTranslator.Cpu.Translation;
using LinuxBinaryTranslator.Memory;

namespace LinuxBinaryTranslator.Cpu
{
    /// <summary>
    /// Translates x86_64 basic blocks from ELF binaries into cached C# delegates.
    /// Uses a translation cache so each block is only decoded once, then executed
    /// natively as managed code on subsequent visits.
    /// </summary>
    public sealed class BlockTranslator
    {
        private readonly VirtualMemoryManager _memory;
        private readonly InstructionDecoder _decoder;
        private readonly ConcurrentDictionary<ulong, CachedBlock> _cache;

        /// <summary>
        /// Callback invoked when a syscall instruction is encountered.
        /// The syscall handler reads arguments from CpuState per the Linux ABI
        /// (RAX=syscall number, RDI/RSI/RDX/R10/R8/R9=args).
        /// </summary>
        public Func<CpuState, VirtualMemoryManager, long>? SyscallHandler { get; set; }

        public BlockTranslator(VirtualMemoryManager memory)
        {
            _memory = memory;
            _decoder = new InstructionDecoder(memory);
            _cache = new ConcurrentDictionary<ulong, CachedBlock>();
        }

        /// <summary>
        /// Get or translate the block starting at the given address.
        /// </summary>
        public CachedBlock GetBlock(ulong address)
        {
            return _cache.GetOrAdd(address, addr => TranslateBlock(addr));
        }

        /// <summary>
        /// Invalidate all cached translations (needed if code is modified).
        /// </summary>
        public void InvalidateCache()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Translate a basic block starting at the given address.
        /// Decodes instructions until a terminator (branch, call, ret, syscall)
        /// is found, then builds a single delegate that executes the entire block.
        /// </summary>
        private CachedBlock TranslateBlock(ulong startAddress)
        {
            var instructions = new System.Collections.Generic.List<DecodedInstruction>();
            ulong addr = startAddress;
            const int MaxBlockSize = 256; // Safety limit

            // Decode instructions until we hit a block terminator
            for (int i = 0; i < MaxBlockSize; i++)
            {
                if (!_memory.IsMapped(addr))
                    break;

                var inst = _decoder.Decode(addr);
                instructions.Add(inst);
                addr += (ulong)inst.Length;

                if (inst.IsTerminator || inst.IsSyscall)
                    break;
            }

            if (instructions.Count == 0)
            {
                // Empty block — return a halt block
                return new CachedBlock(startAddress, 0, (state, mem) =>
                {
                    state.Halted = true;
                    return 0;
                });
            }

            int blockSize = (int)(addr - startAddress);
            var captured = instructions.ToArray();

            // Build a delegate that executes all instructions in this block
            TranslatedBlock blockDelegate = (state, mem) =>
            {
                for (int i = 0; i < captured.Length; i++)
                {
                    ulong next = ExecuteInstruction(captured[i], state, mem);
                    if (state.Halted)
                        return 0;
                    if (next != 0)
                        return next; // Branch/call/ret — next block address
                }
                // Fall through to the next sequential block
                return startAddress + (ulong)blockSize;
            };

            return new CachedBlock(startAddress, blockSize, blockDelegate);
        }

        /// <summary>
        /// Execute a single decoded instruction, modifying CPU state and memory.
        /// Returns 0 to continue sequential execution, or a non-zero address
        /// for control flow transfers (branch, call, ret).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ExecuteInstruction(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem)
        {
            ulong nextAddr = inst.Address + (ulong)inst.Length;

            // Handle syscall first
            if (inst.IsSyscall)
            {
                if (SyscallHandler != null)
                {
                    long result = SyscallHandler(state, mem);
                    state.RAX = (ulong)result;
                }
                state.RIP = nextAddr;
                return nextAddr;
            }

            byte opcode = inst.Opcode[0];

            // Two-byte opcodes
            if (opcode == 0x0F && inst.Opcode.Length > 1)
            {
                return ExecuteTwoByteInstruction(inst, state, mem, nextAddr);
            }

            return ExecuteOneByteInstruction(inst, opcode, state, mem, nextAddr);
        }

        private ulong ExecuteOneByteInstruction(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            switch (opcode)
            {
                // NOP
                case 0x90:
                    return 0;

                // PUSH r64 (50+rd)
                case 0x50: case 0x51: case 0x52: case 0x53:
                case 0x54: case 0x55: case 0x56: case 0x57:
                {
                    int reg = (opcode - 0x50) | (inst.RexB ? 8 : 0);
                    state.Push(mem, state.GetGpr(reg));
                    return 0;
                }

                // POP r64 (58+rd)
                case 0x58: case 0x59: case 0x5A: case 0x5B:
                case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                {
                    int reg = (opcode - 0x58) | (inst.RexB ? 8 : 0);
                    state.SetGpr(reg, state.Pop(mem));
                    return 0;
                }

                // PUSH imm8 (6A) / PUSH imm32 (68)
                case 0x6A: case 0x68:
                    state.Push(mem, (ulong)inst.Immediate);
                    return 0;

                // MOV r/m, r (89) — 32/64-bit
                case 0x89:
                {
                    ulong val = state.GetGpr(inst.Reg);
                    if (inst.Mod == 3)
                    {
                        if (inst.RexW)
                            state.SetGpr(inst.RM, val);
                        else
                            state.SetGpr32(inst.RM, (uint)val);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        if (inst.RexW)
                            mem.WriteUInt64(addr, val);
                        else
                            mem.WriteUInt32(addr, (uint)val);
                    }
                    return 0;
                }

                // MOV r/m8, r8 (88)
                case 0x88:
                {
                    byte val = (byte)state.GetGpr(inst.Reg);
                    if (inst.Mod == 3)
                        SetRegByte(state, inst.RM, inst.HasRex, val);
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        mem.WriteByte(addr, val);
                    }
                    return 0;
                }

                // MOV r, r/m (8B) — 32/64-bit
                case 0x8B:
                {
                    ulong val;
                    if (inst.Mod == 3)
                        val = state.GetGpr(inst.RM);
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        val = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr);
                    }
                    if (inst.RexW)
                        state.SetGpr(inst.Reg, val);
                    else
                        state.SetGpr32(inst.Reg, (uint)val);
                    return 0;
                }

                // MOV r8, r/m8 (8A)
                case 0x8A:
                {
                    byte val;
                    if (inst.Mod == 3)
                        val = (byte)state.GetGpr(inst.RM);
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        val = mem.ReadByte(addr);
                    }
                    SetRegByte(state, inst.Reg, inst.HasRex, val);
                    return 0;
                }

                // MOV r/m, imm32/64 (C7)
                case 0xC7:
                {
                    if (inst.Mod == 3)
                    {
                        if (inst.RexW)
                            state.SetGpr(inst.RM, (ulong)(long)inst.Immediate);
                        else
                            state.SetGpr32(inst.RM, (uint)inst.Immediate);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        if (inst.RexW)
                            mem.WriteUInt64(addr, (ulong)(long)inst.Immediate);
                        else
                            mem.WriteUInt32(addr, (uint)inst.Immediate);
                    }
                    return 0;
                }

                // MOV r/m8, imm8 (C6)
                case 0xC6:
                {
                    if (inst.Mod == 3)
                        SetRegByte(state, inst.RM, inst.HasRex, (byte)inst.Immediate);
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        mem.WriteByte(addr, (byte)inst.Immediate);
                    }
                    return 0;
                }

                // MOV r64, imm64 / MOV r32, imm32 (B8+rd)
                case 0xB8: case 0xB9: case 0xBA: case 0xBB:
                case 0xBC: case 0xBD: case 0xBE: case 0xBF:
                {
                    int reg = (opcode - 0xB8) | (inst.RexB ? 8 : 0);
                    if (inst.RexW)
                        state.SetGpr(reg, (ulong)inst.Immediate);
                    else
                        state.SetGpr32(reg, (uint)inst.Immediate);
                    return 0;
                }

                // MOV r8, imm8 (B0+rb)
                case 0xB0: case 0xB1: case 0xB2: case 0xB3:
                case 0xB4: case 0xB5: case 0xB6: case 0xB7:
                {
                    int reg = (opcode - 0xB0) | (inst.RexB ? 8 : 0);
                    SetRegByte(state, reg, inst.HasRex, (byte)inst.Immediate);
                    return 0;
                }

                // LEA r, m (8D)
                case 0x8D:
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    if (inst.RexW)
                        state.SetGpr(inst.Reg, addr);
                    else
                        state.SetGpr32(inst.Reg, (uint)addr);
                    return 0;
                }

                // ALU operations: ADD (00-05), OR (08-0D), AND (20-25), SUB (28-2D), XOR (30-35), CMP (38-3D)
                case 0x01: case 0x09: case 0x21: case 0x29: case 0x31: case 0x39:
                    return ExecuteAluRmR(inst, opcode, state, mem, nextAddr, false);
                case 0x03: case 0x0B: case 0x23: case 0x2B: case 0x33: case 0x3B:
                    return ExecuteAluRRm(inst, opcode, state, mem, nextAddr, false);
                case 0x00: case 0x08: case 0x20: case 0x28: case 0x30: case 0x38:
                    return ExecuteAluRmR(inst, opcode, state, mem, nextAddr, true);
                case 0x02: case 0x0A: case 0x22: case 0x2A: case 0x32: case 0x3A:
                    return ExecuteAluRRm(inst, opcode, state, mem, nextAddr, true);

                // ALU AL/RAX, imm
                case 0x04: // ADD AL, imm8
                    state.AL = DoAlu8(0, state.AL, (byte)inst.Immediate, state);
                    return 0;
                case 0x05: // ADD RAX/EAX, imm32
                    if (inst.RexW)
                        state.RAX = DoAlu64(0, state.RAX, (ulong)(long)inst.Immediate, state);
                    else
                        state.EAX = DoAlu32(0, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x2C: // SUB AL, imm8
                    state.AL = DoAlu8(5, state.AL, (byte)inst.Immediate, state);
                    return 0;
                case 0x2D: // SUB RAX/EAX, imm32
                    if (inst.RexW)
                        state.RAX = DoAlu64(5, state.RAX, (ulong)(long)inst.Immediate, state);
                    else
                        state.EAX = DoAlu32(5, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x0C: state.AL = DoAlu8(1, state.AL, (byte)inst.Immediate, state); return 0; // OR AL
                case 0x0D: // OR EAX/RAX
                    if (inst.RexW) state.RAX = DoAlu64(1, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(1, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x24: state.AL = DoAlu8(4, state.AL, (byte)inst.Immediate, state); return 0; // AND AL
                case 0x25: // AND EAX/RAX
                    if (inst.RexW) state.RAX = DoAlu64(4, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(4, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x34: state.AL = DoAlu8(6, state.AL, (byte)inst.Immediate, state); return 0; // XOR AL
                case 0x35: // XOR EAX/RAX
                    if (inst.RexW) state.RAX = DoAlu64(6, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(6, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x3C: DoAlu8(7, state.AL, (byte)inst.Immediate, state); return 0; // CMP AL
                case 0x3D: // CMP EAX/RAX
                    if (inst.RexW) DoAlu64(7, state.RAX, (ulong)(long)inst.Immediate, state);
                    else DoAlu32(7, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x1C: state.AL = DoAlu8(3, state.AL, (byte)inst.Immediate, state); return 0; // SBB AL
                case 0x1D:
                    if (inst.RexW) state.RAX = DoAlu64(3, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(3, state.EAX, (uint)inst.Immediate, state);
                    return 0;
                case 0x14: state.AL = DoAlu8(2, state.AL, (byte)inst.Immediate, state); return 0; // ADC AL
                case 0x15:
                    if (inst.RexW) state.RAX = DoAlu64(2, state.RAX, (ulong)(long)inst.Immediate, state);
                    else state.EAX = DoAlu32(2, state.EAX, (uint)inst.Immediate, state);
                    return 0;

                // Group 1: ADD/OR/ADC/SBB/AND/SUB/XOR/CMP r/m, imm (81/83)
                case 0x80: case 0x81: case 0x83:
                    return ExecuteGroup1(inst, opcode, state, mem, nextAddr);

                // TEST r/m, r (84/85)
                case 0x84:
                {
                    byte a = (inst.Mod == 3) ? (byte)state.GetGpr(inst.RM) : mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    byte b = (byte)state.GetGpr(inst.Reg);
                    byte result = (byte)(a & b);
                    state.SetFlag(X86Flags.ZF, result == 0);
                    state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }
                case 0x85:
                {
                    ulong a, b;
                    if (inst.Mod == 3)
                        a = state.GetGpr(inst.RM);
                    else
                        a = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    b = state.GetGpr(inst.Reg);
                    ulong result = a & b;
                    if (inst.RexW) state.UpdateFlags64(result);
                    else state.UpdateFlags32((uint)result);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }

                // TEST AL/RAX, imm (A8/A9)
                case 0xA8:
                {
                    byte result = (byte)(state.AL & (byte)inst.Immediate);
                    state.SetFlag(X86Flags.ZF, result == 0);
                    state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }
                case 0xA9:
                {
                    if (inst.RexW)
                    {
                        ulong result = state.RAX & (ulong)(long)inst.Immediate;
                        state.UpdateFlags64(result);
                    }
                    else
                    {
                        uint result = state.EAX & (uint)inst.Immediate;
                        state.UpdateFlags32(result);
                    }
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }

                // XCHG r, r/m (87)
                case 0x87:
                {
                    ulong a = state.GetGpr(inst.Reg);
                    ulong b;
                    if (inst.Mod == 3)
                    {
                        b = state.GetGpr(inst.RM);
                        state.SetGpr(inst.RM, a);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        b = inst.RexW ? mem.ReadUInt64(addr) : mem.ReadUInt32(addr);
                        if (inst.RexW) mem.WriteUInt64(addr, a);
                        else mem.WriteUInt32(addr, (uint)a);
                    }
                    if (inst.RexW) state.SetGpr(inst.Reg, b);
                    else state.SetGpr32(inst.Reg, (uint)b);
                    return 0;
                }

                // Shift group (C1 r/m, imm8)
                case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                    return ExecuteShiftGroup(inst, opcode, state, mem, nextAddr);

                // INC/DEC/CALL/JMP/PUSH — group FF
                case 0xFF:
                    return ExecuteGroupFF(inst, state, mem, nextAddr);

                // NOT/NEG/MUL/IMUL/DIV/IDIV (F7)
                case 0xF7:
                    return ExecuteGroupF7(inst, state, mem, nextAddr);
                case 0xF6:
                    return ExecuteGroupF6(inst, state, mem, nextAddr);

                // IMUL r, r/m, imm (69/6B)
                case 0x69: case 0x6B:
                {
                    ulong src;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    long result = (long)src * inst.Immediate;
                    if (inst.RexW) state.SetGpr(inst.Reg, (ulong)result);
                    else state.SetGpr32(inst.Reg, (uint)result);
                    return 0;
                }

                // RET (C3)
                case 0xC3:
                {
                    ulong retAddr = state.Pop(mem);
                    state.RIP = retAddr;
                    return retAddr;
                }

                // RET imm16 (C2)
                case 0xC2:
                {
                    ulong retAddr = state.Pop(mem);
                    state.RSP += (ulong)inst.Immediate;
                    state.RIP = retAddr;
                    return retAddr;
                }

                // LEAVE (C9)
                case 0xC9:
                    state.RSP = state.RBP;
                    state.RBP = state.Pop(mem);
                    return 0;

                // JMP rel8 (EB) / JMP rel32 (E9)
                case 0xEB: case 0xE9:
                {
                    ulong target = nextAddr + (ulong)(long)inst.Immediate;
                    state.RIP = target;
                    return target;
                }

                // CALL rel32 (E8)
                case 0xE8:
                {
                    state.Push(mem, nextAddr);
                    ulong target = nextAddr + (ulong)(long)inst.Immediate;
                    state.RIP = target;
                    return target;
                }

                // Jcc rel8 (70-7F)
                case 0x70: case 0x71: case 0x72: case 0x73:
                case 0x74: case 0x75: case 0x76: case 0x77:
                case 0x78: case 0x79: case 0x7A: case 0x7B:
                case 0x7C: case 0x7D: case 0x7E: case 0x7F:
                {
                    int cc = opcode - 0x70;
                    if (EvaluateCondition(cc, state))
                    {
                        ulong target = nextAddr + (ulong)(long)inst.Immediate;
                        state.RIP = target;
                        return target;
                    }
                    return nextAddr;
                }

                // INT 0x80 (CD 80) — 32-bit Linux syscall ABI
                case 0xCD:
                    if (inst.Immediate == 0x80 && SyscallHandler != null)
                    {
                        long result = SyscallHandler(state, mem);
                        state.RAX = (ulong)result;
                    }
                    state.RIP = nextAddr;
                    return nextAddr;

                // CBW/CWDE/CDQE (98)
                case 0x98:
                    if (inst.RexW)
                        state.RAX = (ulong)(long)(int)state.EAX; // CDQE
                    else
                        state.EAX = (uint)(int)(short)state.AX; // CWDE
                    return 0;

                // CWD/CDQ/CQO (99)
                case 0x99:
                    if (inst.RexW)
                        state.RDX = ((long)state.RAX < 0) ? 0xFFFFFFFFFFFFFFFFUL : 0; // CQO
                    else
                        state.EDX = ((int)state.EAX < 0) ? 0xFFFFFFFFU : 0; // CDQ
                    return 0;

                // CLC (F8), STC (F9), CMC (F5)
                case 0xF8: state.SetFlag(X86Flags.CF, false); return 0;
                case 0xF9: state.SetFlag(X86Flags.CF, true); return 0;
                case 0xF5: state.SetFlag(X86Flags.CF, !state.GetFlag(X86Flags.CF)); return 0;

                // CLD (FC), STD (FD)
                case 0xFC: state.SetFlag(X86Flags.DF, false); return 0;
                case 0xFD: state.SetFlag(X86Flags.DF, true); return 0;

                // STOSB/STOSQ (AA/AB) — store AL/RAX at [RDI], advance RDI
                case 0xAA:
                    mem.WriteByte(state.RDI, state.AL);
                    state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-1L) : 1;
                    return 0;
                case 0xAB:
                    if (inst.RexW)
                    {
                        mem.WriteUInt64(state.RDI, state.RAX);
                        state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-8L) : 8;
                    }
                    else
                    {
                        mem.WriteUInt32(state.RDI, state.EAX);
                        state.RDI += state.GetFlag(X86Flags.DF) ? unchecked((ulong)-4L) : 4;
                    }
                    return 0;

                // MOVSB/MOVSQ (A4/A5) — move [RSI] to [RDI]
                case 0xA4:
                    mem.WriteByte(state.RDI, mem.ReadByte(state.RSI));
                    if (state.GetFlag(X86Flags.DF)) { state.RSI--; state.RDI--; }
                    else { state.RSI++; state.RDI++; }
                    return 0;
                case 0xA5:
                    if (inst.RexW)
                    {
                        mem.WriteUInt64(state.RDI, mem.ReadUInt64(state.RSI));
                        if (state.GetFlag(X86Flags.DF)) { state.RSI -= 8; state.RDI -= 8; }
                        else { state.RSI += 8; state.RDI += 8; }
                    }
                    else
                    {
                        mem.WriteUInt32(state.RDI, mem.ReadUInt32(state.RSI));
                        if (state.GetFlag(X86Flags.DF)) { state.RSI -= 4; state.RDI -= 4; }
                        else { state.RSI += 4; state.RDI += 4; }
                    }
                    return 0;

                // HLT (F4)
                case 0xF4:
                    state.Halted = true;
                    return 0;

                default:
                    // Unimplemented — advance to next instruction
                    return 0;
            }
        }

        private ulong ExecuteTwoByteInstruction(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            byte second = inst.Opcode[1];

            switch (second)
            {
                // Jcc rel32 (0F 80-8F)
                case 0x80: case 0x81: case 0x82: case 0x83:
                case 0x84: case 0x85: case 0x86: case 0x87:
                case 0x88: case 0x89: case 0x8A: case 0x8B:
                case 0x8C: case 0x8D: case 0x8E: case 0x8F:
                {
                    int cc = second - 0x80;
                    if (EvaluateCondition(cc, state))
                    {
                        ulong target = nextAddr + (ulong)(long)inst.Immediate;
                        state.RIP = target;
                        return target;
                    }
                    return nextAddr;
                }

                // SETcc (0F 90-9F)
                case 0x90: case 0x91: case 0x92: case 0x93:
                case 0x94: case 0x95: case 0x96: case 0x97:
                case 0x98: case 0x99: case 0x9A: case 0x9B:
                case 0x9C: case 0x9D: case 0x9E: case 0x9F:
                {
                    int cc = second - 0x90;
                    byte val = EvaluateCondition(cc, state) ? (byte)1 : (byte)0;
                    if (inst.Mod == 3)
                        SetRegByte(state, inst.RM, inst.HasRex, val);
                    else
                        mem.WriteByte(ComputeEffectiveAddress(inst, state, mem, nextAddr), val);
                    return 0;
                }

                // CMOVcc (0F 40-4F)
                case 0x40: case 0x41: case 0x42: case 0x43:
                case 0x44: case 0x45: case 0x46: case 0x47:
                case 0x48: case 0x49: case 0x4A: case 0x4B:
                case 0x4C: case 0x4D: case 0x4E: case 0x4F:
                {
                    int cc = second - 0x40;
                    if (EvaluateCondition(cc, state))
                    {
                        ulong val;
                        if (inst.Mod == 3) val = state.GetGpr(inst.RM);
                        else val = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                        if (inst.RexW) state.SetGpr(inst.Reg, val);
                        else state.SetGpr32(inst.Reg, (uint)val);
                    }
                    return 0;
                }

                // MOVZX r, r/m8 (0F B6)
                case 0xB6:
                {
                    byte val;
                    if (inst.Mod == 3)
                        val = (byte)state.GetGpr(inst.RM);
                    else
                        val = mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW) state.SetGpr(inst.Reg, val);
                    else state.SetGpr32(inst.Reg, val);
                    return 0;
                }

                // MOVZX r, r/m16 (0F B7)
                case 0xB7:
                {
                    ushort val;
                    if (inst.Mod == 3)
                        val = (ushort)state.GetGpr(inst.RM);
                    else
                        val = mem.ReadUInt16(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW) state.SetGpr(inst.Reg, val);
                    else state.SetGpr32(inst.Reg, val);
                    return 0;
                }

                // MOVSX r, r/m8 (0F BE)
                case 0xBE:
                {
                    sbyte val;
                    if (inst.Mod == 3)
                        val = (sbyte)(byte)state.GetGpr(inst.RM);
                    else
                        val = (sbyte)mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW) state.SetGpr(inst.Reg, (ulong)(long)val);
                    else state.SetGpr32(inst.Reg, (uint)(int)val);
                    return 0;
                }

                // MOVSX r, r/m16 (0F BF)
                case 0xBF:
                {
                    short val;
                    if (inst.Mod == 3)
                        val = (short)(ushort)state.GetGpr(inst.RM);
                    else
                        val = (short)mem.ReadUInt16(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW) state.SetGpr(inst.Reg, (ulong)(long)val);
                    else state.SetGpr32(inst.Reg, (uint)(int)val);
                    return 0;
                }

                // IMUL r, r/m (0F AF)
                case 0xAF:
                {
                    ulong src;
                    if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                    else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                    if (inst.RexW)
                    {
                        long result = (long)state.GetGpr(inst.Reg) * (long)src;
                        state.SetGpr(inst.Reg, (ulong)result);
                    }
                    else
                    {
                        int result = (int)state.GetGpr(inst.Reg) * (int)src;
                        state.SetGpr32(inst.Reg, (uint)result);
                    }
                    return 0;
                }

                // CPUID (0F A2) — return minimal identification
                case 0xA2:
                    ExecuteCpuid(state);
                    return 0;

                // RDTSC (0F 31) — return a monotonic counter
                case 0x31:
                {
                    ulong tsc = (ulong)Environment.TickCount * 1000000UL;
                    state.EAX = (uint)tsc;
                    state.EDX = (uint)(tsc >> 32);
                    return 0;
                }

                // Multi-byte NOP (0F 1F)
                case 0x1F:
                    return 0;

                default:
                    return 0;
            }
        }

        // === ALU helpers ===

        private ulong ExecuteAluRmR(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr, bool byte_op)
        {
            int aluOp = (opcode >> 3) & 7;
            if (byte_op)
            {
                byte src = (byte)state.GetGpr(inst.Reg);
                if (inst.Mod == 3)
                {
                    byte dst = (byte)state.GetGpr(inst.RM);
                    byte result = DoAlu8(aluOp, dst, src, state);
                    if (aluOp != 7) SetRegByte(state, inst.RM, inst.HasRex, result);
                }
                else
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    byte dst = mem.ReadByte(addr);
                    byte result = DoAlu8(aluOp, dst, src, state);
                    if (aluOp != 7) mem.WriteByte(addr, result);
                }
            }
            else
            {
                ulong src = state.GetGpr(inst.Reg);
                if (inst.Mod == 3)
                {
                    ulong dst = state.GetGpr(inst.RM);
                    if (inst.RexW)
                    {
                        ulong result = DoAlu64(aluOp, dst, src, state);
                        if (aluOp != 7) state.SetGpr(inst.RM, result);
                    }
                    else
                    {
                        uint result = DoAlu32(aluOp, (uint)dst, (uint)src, state);
                        if (aluOp != 7) state.SetGpr32(inst.RM, result);
                    }
                }
                else
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    if (inst.RexW)
                    {
                        ulong dst = mem.ReadUInt64(addr);
                        ulong result = DoAlu64(aluOp, dst, src, state);
                        if (aluOp != 7) mem.WriteUInt64(addr, result);
                    }
                    else
                    {
                        uint dst = mem.ReadUInt32(addr);
                        uint result = DoAlu32(aluOp, dst, (uint)src, state);
                        if (aluOp != 7) mem.WriteUInt32(addr, result);
                    }
                }
            }
            return 0;
        }

        private ulong ExecuteAluRRm(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr, bool byte_op)
        {
            int aluOp = (opcode >> 3) & 7;
            if (byte_op)
            {
                byte src;
                if (inst.Mod == 3) src = (byte)state.GetGpr(inst.RM);
                else src = mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                byte dst = (byte)state.GetGpr(inst.Reg);
                byte result = DoAlu8(aluOp, dst, src, state);
                if (aluOp != 7) SetRegByte(state, inst.Reg, inst.HasRex, result);
            }
            else
            {
                ulong src;
                if (inst.Mod == 3) src = state.GetGpr(inst.RM);
                else src = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                if (inst.RexW)
                {
                    ulong result = DoAlu64(aluOp, state.GetGpr(inst.Reg), src, state);
                    if (aluOp != 7) state.SetGpr(inst.Reg, result);
                }
                else
                {
                    uint result = DoAlu32(aluOp, (uint)state.GetGpr(inst.Reg), (uint)src, state);
                    if (aluOp != 7) state.SetGpr32(inst.Reg, result);
                }
            }
            return 0;
        }

        private ulong ExecuteGroup1(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int aluOp = (inst.ModRM >> 3) & 7;

            if (opcode == 0x80)
            {
                byte src = (byte)inst.Immediate;
                if (inst.Mod == 3)
                {
                    byte dst = (byte)state.GetGpr(inst.RM);
                    byte result = DoAlu8(aluOp, dst, src, state);
                    if (aluOp != 7) SetRegByte(state, inst.RM, inst.HasRex, result);
                }
                else
                {
                    ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                    byte dst = mem.ReadByte(addr);
                    byte result = DoAlu8(aluOp, dst, src, state);
                    if (aluOp != 7) mem.WriteByte(addr, result);
                }
                return 0;
            }

            // 81 and 83
            ulong immVal = (opcode == 0x83) ? (ulong)(long)(sbyte)(byte)inst.Immediate : (ulong)(long)(int)inst.Immediate;

            if (inst.Mod == 3)
            {
                if (inst.RexW)
                {
                    ulong dst = state.GetGpr(inst.RM);
                    ulong result = DoAlu64(aluOp, dst, immVal, state);
                    if (aluOp != 7) state.SetGpr(inst.RM, result);
                }
                else
                {
                    uint dst = (uint)state.GetGpr(inst.RM);
                    uint result = DoAlu32(aluOp, dst, (uint)immVal, state);
                    if (aluOp != 7) state.SetGpr32(inst.RM, result);
                }
            }
            else
            {
                ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                if (inst.RexW)
                {
                    ulong dst = mem.ReadUInt64(addr);
                    ulong result = DoAlu64(aluOp, dst, immVal, state);
                    if (aluOp != 7) mem.WriteUInt64(addr, result);
                }
                else
                {
                    uint dst = mem.ReadUInt32(addr);
                    uint result = DoAlu32(aluOp, dst, (uint)immVal, state);
                    if (aluOp != 7) mem.WriteUInt32(addr, result);
                }
            }
            return 0;
        }

        private ulong ExecuteShiftGroup(DecodedInstruction inst, byte opcode, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int shiftOp = (inst.ModRM >> 3) & 7;
            int count;

            switch (opcode)
            {
                case 0xD0: case 0xD1: count = 1; break;
                case 0xD2: case 0xD3: count = (int)(state.RCX & 0x3F); break;
                default: count = (int)inst.Immediate & 0x3F; break;
            }

            bool isByte = (opcode == 0xC0 || opcode == 0xD0 || opcode == 0xD2);

            if (isByte)
            {
                byte val;
                if (inst.Mod == 3) val = (byte)state.GetGpr(inst.RM);
                else val = mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                byte result = DoShift8(shiftOp, val, count, state);
                if (inst.Mod == 3) SetRegByte(state, inst.RM, inst.HasRex, result);
                else mem.WriteByte(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
            }
            else if (inst.RexW)
            {
                ulong val;
                if (inst.Mod == 3) val = state.GetGpr(inst.RM);
                else val = mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                ulong result = DoShift64(shiftOp, val, count, state);
                if (inst.Mod == 3) state.SetGpr(inst.RM, result);
                else mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
            }
            else
            {
                uint val;
                if (inst.Mod == 3) val = (uint)state.GetGpr(inst.RM);
                else val = mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));
                uint result = DoShift32(shiftOp, val, count, state);
                if (inst.Mod == 3) state.SetGpr32(inst.RM, result);
                else mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
            }
            return 0;
        }

        private ulong ExecuteGroupFF(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int regOp = (inst.ModRM >> 3) & 7;
            ulong operand;
            if (inst.Mod == 3)
                operand = state.GetGpr(inst.RM);
            else
                operand = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));

            switch (regOp)
            {
                case 0: // INC
                    if (inst.RexW)
                    {
                        ulong result = operand + 1;
                        state.UpdateFlags64(result);
                        state.SetFlag(X86Flags.OF, operand == 0x7FFFFFFFFFFFFFFFUL);
                        if (inst.Mod == 3) state.SetGpr(inst.RM, result);
                        else mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    else
                    {
                        uint result = (uint)operand + 1;
                        state.UpdateFlags32(result);
                        state.SetFlag(X86Flags.OF, (uint)operand == 0x7FFFFFFFU);
                        if (inst.Mod == 3) state.SetGpr32(inst.RM, result);
                        else mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    return 0;

                case 1: // DEC
                    if (inst.RexW)
                    {
                        ulong result = operand - 1;
                        state.UpdateFlags64(result);
                        state.SetFlag(X86Flags.OF, operand == 0x8000000000000000UL);
                        if (inst.Mod == 3) state.SetGpr(inst.RM, result);
                        else mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    else
                    {
                        uint result = (uint)operand - 1;
                        state.UpdateFlags32(result);
                        state.SetFlag(X86Flags.OF, (uint)operand == 0x80000000U);
                        if (inst.Mod == 3) state.SetGpr32(inst.RM, result);
                        else mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    return 0;

                case 2: // CALL indirect
                    state.Push(mem, nextAddr);
                    state.RIP = operand;
                    return operand;

                case 4: // JMP indirect
                    state.RIP = operand;
                    return operand;

                case 6: // PUSH r/m
                    state.Push(mem, operand);
                    return 0;

                default:
                    return 0;
            }
        }

        private ulong ExecuteGroupF7(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int regOp = (inst.ModRM >> 3) & 7;
            ulong operand;
            if (inst.Mod == 3) operand = state.GetGpr(inst.RM);
            else operand = inst.RexW ? mem.ReadUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr)) : mem.ReadUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr));

            switch (regOp)
            {
                case 0: // TEST r/m, imm32
                {
                    ulong result = operand & (ulong)(long)(int)inst.Immediate;
                    if (inst.RexW) state.UpdateFlags64(result);
                    else state.UpdateFlags32((uint)result);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }
                case 2: // NOT
                {
                    ulong result = ~operand;
                    if (inst.Mod == 3)
                    {
                        if (inst.RexW) state.SetGpr(inst.RM, result);
                        else state.SetGpr32(inst.RM, (uint)result);
                    }
                    else
                    {
                        ulong addr = ComputeEffectiveAddress(inst, state, mem, nextAddr);
                        if (inst.RexW) mem.WriteUInt64(addr, result);
                        else mem.WriteUInt32(addr, (uint)result);
                    }
                    return 0;
                }
                case 3: // NEG
                {
                    if (inst.RexW)
                    {
                        ulong result = (ulong)(-(long)operand);
                        state.UpdateFlags64(result);
                        state.SetFlag(X86Flags.CF, operand != 0);
                        if (inst.Mod == 3) state.SetGpr(inst.RM, result);
                        else mem.WriteUInt64(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    else
                    {
                        uint result = (uint)(-(int)(uint)operand);
                        state.UpdateFlags32(result);
                        state.SetFlag(X86Flags.CF, (uint)operand != 0);
                        if (inst.Mod == 3) state.SetGpr32(inst.RM, result);
                        else mem.WriteUInt32(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    }
                    return 0;
                }
                case 4: // MUL (unsigned)
                    if (inst.RexW)
                    {
                        // 128-bit result: RDX:RAX = RAX * operand
                        UInt128Multiply(state.RAX, operand, out ulong lo, out ulong hi);
                        state.RAX = lo;
                        state.RDX = hi;
                        state.SetFlag(X86Flags.CF, hi != 0);
                        state.SetFlag(X86Flags.OF, hi != 0);
                    }
                    else
                    {
                        ulong result = (ulong)state.EAX * (uint)operand;
                        state.EAX = (uint)result;
                        state.EDX = (uint)(result >> 32);
                        state.SetFlag(X86Flags.CF, state.EDX != 0);
                        state.SetFlag(X86Flags.OF, state.EDX != 0);
                    }
                    return 0;
                case 5: // IMUL (signed, single-operand)
                    if (inst.RexW)
                    {
                        long sResult = (long)state.RAX * (long)operand;
                        state.RAX = (ulong)sResult;
                        state.RDX = (ulong)(sResult >> 63); // sign extension
                    }
                    else
                    {
                        long sResult = (int)state.EAX * (long)(int)(uint)operand;
                        state.EAX = (uint)sResult;
                        state.EDX = (uint)(sResult >> 32);
                    }
                    return 0;
                case 6: // DIV (unsigned)
                    if (inst.RexW)
                    {
                        if (operand == 0) { state.Halted = true; state.ExitCode = 136; return 0; }
                        // For simplicity, handle the common case where RDX is 0
                        if (state.RDX == 0)
                        {
                            state.RAX = state.RAX / operand;
                            state.RDX = state.RAX % operand;
                        }
                        else
                        {
                            // Full 128÷64 division would go here; simplified for basic binaries
                            state.RAX = state.RAX / operand;
                            state.RDX = state.RAX % operand;
                        }
                    }
                    else
                    {
                        uint divisor = (uint)operand;
                        if (divisor == 0) { state.Halted = true; state.ExitCode = 136; return 0; }
                        ulong dividend = ((ulong)state.EDX << 32) | state.EAX;
                        state.EAX = (uint)(dividend / divisor);
                        state.EDX = (uint)(dividend % divisor);
                    }
                    return 0;
                case 7: // IDIV (signed)
                    if (inst.RexW)
                    {
                        long divisor = (long)operand;
                        if (divisor == 0) { state.Halted = true; state.ExitCode = 136; return 0; }
                        long dividend = (long)state.RAX;
                        state.RAX = (ulong)(dividend / divisor);
                        state.RDX = (ulong)(dividend % divisor);
                    }
                    else
                    {
                        int divisor = (int)(uint)operand;
                        if (divisor == 0) { state.Halted = true; state.ExitCode = 136; return 0; }
                        long dividend = ((long)state.EDX << 32) | state.EAX;
                        state.EAX = (uint)(int)(dividend / divisor);
                        state.EDX = (uint)(int)(dividend % divisor);
                    }
                    return 0;
                default:
                    return 0;
            }
        }

        private ulong ExecuteGroupF6(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int regOp = (inst.ModRM >> 3) & 7;
            byte operand;
            if (inst.Mod == 3) operand = (byte)state.GetGpr(inst.RM);
            else operand = mem.ReadByte(ComputeEffectiveAddress(inst, state, mem, nextAddr));

            switch (regOp)
            {
                case 0: // TEST r/m8, imm8
                {
                    byte result = (byte)(operand & (byte)inst.Immediate);
                    state.SetFlag(X86Flags.ZF, result == 0);
                    state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    return 0;
                }
                case 2: // NOT r/m8
                    if (inst.Mod == 3) SetRegByte(state, inst.RM, inst.HasRex, (byte)~operand);
                    else mem.WriteByte(ComputeEffectiveAddress(inst, state, mem, nextAddr), (byte)~operand);
                    return 0;
                case 3: // NEG r/m8
                {
                    byte result = (byte)(-(sbyte)operand);
                    state.SetFlag(X86Flags.CF, operand != 0);
                    state.SetFlag(X86Flags.ZF, result == 0);
                    state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
                    if (inst.Mod == 3) SetRegByte(state, inst.RM, inst.HasRex, result);
                    else mem.WriteByte(ComputeEffectiveAddress(inst, state, mem, nextAddr), result);
                    return 0;
                }
                default:
                    return 0;
            }
        }

        // === Condition evaluation for Jcc/SETcc/CMOVcc ===
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool EvaluateCondition(int cc, CpuState state)
        {
            return cc switch
            {
                0x0 => state.GetFlag(X86Flags.OF),                                          // O
                0x1 => !state.GetFlag(X86Flags.OF),                                         // NO
                0x2 => state.GetFlag(X86Flags.CF),                                          // B/C/NAE
                0x3 => !state.GetFlag(X86Flags.CF),                                         // NB/NC/AE
                0x4 => state.GetFlag(X86Flags.ZF),                                          // E/Z
                0x5 => !state.GetFlag(X86Flags.ZF),                                         // NE/NZ
                0x6 => state.GetFlag(X86Flags.CF) || state.GetFlag(X86Flags.ZF),            // BE/NA
                0x7 => !state.GetFlag(X86Flags.CF) && !state.GetFlag(X86Flags.ZF),          // NBE/A
                0x8 => state.GetFlag(X86Flags.SF),                                          // S
                0x9 => !state.GetFlag(X86Flags.SF),                                         // NS
                0xA => state.GetFlag(X86Flags.PF),                                          // P/PE
                0xB => !state.GetFlag(X86Flags.PF),                                         // NP/PO
                0xC => state.GetFlag(X86Flags.SF) != state.GetFlag(X86Flags.OF),            // L/NGE
                0xD => state.GetFlag(X86Flags.SF) == state.GetFlag(X86Flags.OF),            // NL/GE
                0xE => state.GetFlag(X86Flags.ZF) || (state.GetFlag(X86Flags.SF) != state.GetFlag(X86Flags.OF)), // LE/NG
                0xF => !state.GetFlag(X86Flags.ZF) && (state.GetFlag(X86Flags.SF) == state.GetFlag(X86Flags.OF)), // NLE/G
                _ => false
            };
        }

        // === ALU operations ===
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong DoAlu64(int op, ulong a, ulong b, CpuState state)
        {
            ulong result;
            switch (op)
            {
                case 0: // ADD
                    result = a + b;
                    state.SetFlag(X86Flags.CF, result < a);
                    state.SetFlag(X86Flags.OF, ((a ^ result) & (b ^ result) & 0x8000000000000000UL) != 0);
                    break;
                case 1: // OR
                    result = a | b;
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    break;
                case 2: // ADC
                    ulong carry = state.GetFlag(X86Flags.CF) ? 1UL : 0;
                    result = a + b + carry;
                    state.SetFlag(X86Flags.CF, (carry != 0 && result <= a) || (carry == 0 && result < a));
                    state.SetFlag(X86Flags.OF, ((a ^ result) & (b ^ result) & 0x8000000000000000UL) != 0);
                    break;
                case 3: // SBB
                    ulong borrow = state.GetFlag(X86Flags.CF) ? 1UL : 0;
                    result = a - b - borrow;
                    state.SetFlag(X86Flags.CF, a < b + borrow);
                    state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x8000000000000000UL) != 0);
                    break;
                case 4: // AND
                    result = a & b;
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    break;
                case 5: // SUB
                    result = a - b;
                    state.SetFlag(X86Flags.CF, a < b);
                    state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x8000000000000000UL) != 0);
                    break;
                case 6: // XOR
                    result = a ^ b;
                    state.SetFlag(X86Flags.CF, false);
                    state.SetFlag(X86Flags.OF, false);
                    break;
                case 7: // CMP (same as SUB but result is discarded)
                    result = a - b;
                    state.SetFlag(X86Flags.CF, a < b);
                    state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x8000000000000000UL) != 0);
                    break;
                default:
                    result = 0;
                    break;
            }
            state.UpdateFlags64(result);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint DoAlu32(int op, uint a, uint b, CpuState state)
        {
            uint result;
            switch (op)
            {
                case 0: result = a + b; state.SetFlag(X86Flags.CF, result < a); state.SetFlag(X86Flags.OF, ((a ^ result) & (b ^ result) & 0x80000000U) != 0); break;
                case 1: result = a | b; state.SetFlag(X86Flags.CF, false); state.SetFlag(X86Flags.OF, false); break;
                case 2: { uint c = state.GetFlag(X86Flags.CF) ? 1U : 0; result = a + b + c; state.SetFlag(X86Flags.CF, (c != 0 && result <= a) || (c == 0 && result < a)); state.SetFlag(X86Flags.OF, ((a ^ result) & (b ^ result) & 0x80000000U) != 0); break; }
                case 3: { uint c = state.GetFlag(X86Flags.CF) ? 1U : 0; result = a - b - c; state.SetFlag(X86Flags.CF, a < b + c); state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x80000000U) != 0); break; }
                case 4: result = a & b; state.SetFlag(X86Flags.CF, false); state.SetFlag(X86Flags.OF, false); break;
                case 5: result = a - b; state.SetFlag(X86Flags.CF, a < b); state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x80000000U) != 0); break;
                case 6: result = a ^ b; state.SetFlag(X86Flags.CF, false); state.SetFlag(X86Flags.OF, false); break;
                case 7: result = a - b; state.SetFlag(X86Flags.CF, a < b); state.SetFlag(X86Flags.OF, ((a ^ b) & (a ^ result) & 0x80000000U) != 0); break;
                default: result = 0; break;
            }
            state.UpdateFlags32(result);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte DoAlu8(int op, byte a, byte b, CpuState state)
        {
            byte result;
            switch (op)
            {
                case 0: result = (byte)(a + b); state.SetFlag(X86Flags.CF, result < a); break;
                case 1: result = (byte)(a | b); state.SetFlag(X86Flags.CF, false); break;
                case 4: result = (byte)(a & b); state.SetFlag(X86Flags.CF, false); break;
                case 5: case 7: result = (byte)(a - b); state.SetFlag(X86Flags.CF, a < b); break;
                case 6: result = (byte)(a ^ b); state.SetFlag(X86Flags.CF, false); break;
                default: result = (byte)(a + b); break;
            }
            state.SetFlag(X86Flags.ZF, result == 0);
            state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
            return result;
        }

        // === Shift operations ===
        private static ulong DoShift64(int op, ulong val, int count, CpuState state)
        {
            if (count == 0) return val;
            count &= 63;
            ulong result = op switch
            {
                0 => val << count,               // ROL (simplified)
                1 => val >> count,               // ROR (simplified)
                4 => val << count,               // SHL/SAL
                5 => val >> count,               // SHR
                7 => (ulong)((long)val >> count), // SAR
                _ => val
            };
            if (op == 4 || op == 5 || op == 7)
                state.UpdateFlags64(result);
            return result;
        }

        private static uint DoShift32(int op, uint val, int count, CpuState state)
        {
            if (count == 0) return val;
            count &= 31;
            uint result = op switch
            {
                0 => (val << count) | (val >> (32 - count)),
                1 => (val >> count) | (val << (32 - count)),
                4 => val << count,
                5 => val >> count,
                7 => (uint)((int)val >> count),
                _ => val
            };
            if (op == 4 || op == 5 || op == 7)
                state.UpdateFlags32(result);
            return result;
        }

        private static byte DoShift8(int op, byte val, int count, CpuState state)
        {
            if (count == 0) return val;
            count &= 7;
            byte result = op switch
            {
                4 => (byte)(val << count),
                5 => (byte)(val >> count),
                7 => (byte)((sbyte)val >> count),
                _ => val
            };
            state.SetFlag(X86Flags.ZF, result == 0);
            state.SetFlag(X86Flags.SF, (result & 0x80) != 0);
            return result;
        }

        // === CPUID ===
        private static void ExecuteCpuid(CpuState state)
        {
            uint leaf = state.EAX;
            switch (leaf)
            {
                case 0: // Vendor string
                    state.EAX = 1; // Max leaf
                    state.EBX = 0x756E694C; // "Linu"
                    state.EDX = 0x72547878; // "xxTr"
                    state.ECX = 0x006C6E61; // "anl\0"
                    break;
                case 1: // Feature info
                    state.EAX = 0x000306C3; // Family/model
                    state.EBX = 0;
                    state.ECX = 0; // No SSE4, etc.
                    state.EDX = (1 << 0) | (1 << 4) | (1 << 15); // FPU, TSC, CMOV
                    break;
                default:
                    state.EAX = state.EBX = state.ECX = state.EDX = 0;
                    break;
            }
        }

        // === Effective address computation ===
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ComputeEffectiveAddress(DecodedInstruction inst, CpuState state, VirtualMemoryManager mem, ulong nextAddr)
        {
            int mod = inst.Mod;
            int rm = inst.RM;

            if (mod == 3)
                return state.GetGpr(rm); // Register direct — shouldn't be called for memory ops

            ulong addr;

            if (!inst.HasSIB)
            {
                if (mod == 0 && (rm & 7) == 5)
                {
                    // RIP-relative addressing
                    addr = nextAddr + (ulong)(long)inst.Displacement;
                }
                else
                {
                    addr = state.GetGpr(rm);
                    if (mod == 1 || mod == 2)
                        addr += (ulong)(long)inst.Displacement;
                }
            }
            else
            {
                // SIB addressing
                int baseReg = inst.SIB & 7;
                int indexReg = (inst.SIB >> 3) & 7;
                int scale = (inst.SIB >> 6) & 3;

                if (inst.RexB) baseReg |= 8;
                if (inst.RexX) indexReg |= 8;

                if (mod == 0 && (baseReg & 7) == 5)
                    addr = (ulong)(long)inst.Displacement; // disp32 only
                else
                    addr = state.GetGpr(baseReg);

                if (indexReg != 4) // RSP cannot be index
                    addr += state.GetGpr(indexReg) << scale;

                if (mod == 1 || mod == 2)
                    addr += (ulong)(long)inst.Displacement;
            }

            return addr;
        }

        // === Helper: set byte register ===
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetRegByte(CpuState state, int reg, bool hasRex, byte value)
        {
            if (hasRex || reg < 4)
            {
                // With REX or regs 0-3: low byte of GPR
                ulong current = state.GetGpr(reg);
                state.SetGpr(reg, (current & 0xFFFFFFFFFFFFFF00UL) | value);
            }
            else
            {
                // Without REX, regs 4-7 map to AH/CH/DH/BH
                int hiReg = reg - 4; // 0=AH(RAX), 1=CH(RCX), 2=DH(RDX), 3=BH(RBX)
                ulong current = state.GetGpr(hiReg);
                state.SetGpr(hiReg, (current & 0xFFFFFFFFFFFF00FFUL) | ((ulong)value << 8));
            }
        }

        // === 128-bit multiply helper ===
        private static void UInt128Multiply(ulong a, ulong b, out ulong lo, out ulong hi)
        {
            // Split into 32-bit parts to avoid overflow
            ulong aLo = (uint)a, aHi = a >> 32;
            ulong bLo = (uint)b, bHi = b >> 32;

            ulong p0 = aLo * bLo;
            ulong p1 = aLo * bHi;
            ulong p2 = aHi * bLo;
            ulong p3 = aHi * bHi;

            ulong mid = (p0 >> 32) + (uint)p1 + (uint)p2;
            lo = (uint)p0 | (mid << 32);
            hi = p3 + (p1 >> 32) + (p2 >> 32) + (mid >> 32);
        }
    }
}
