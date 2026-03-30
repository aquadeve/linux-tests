;
; This file is part of Foreign Linux.
;
; Copyright (C) 2014, 2015 Xiangyan Sun <wishstudio@gmail.com>
;
; This program is free software: you can redistribute it and/or modify
; it under the terms of the GNU General Public License as published by
; the Free Software Foundation, either version 3 of the License, or
; (at your option) any later version.
;
; This program is distributed in the hope that it will be useful,
; but WITHOUT ANY WARRANTY; without even the implied warranty of
; MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
; GNU General Public License for more details.
;
; You should have received a copy of the GNU General Public License
; along with this program. If not, see <http://www.gnu.org/licenses/>.
;

.code
M128ASTRUCT
_LowQWORD?
_HighQWORD?
M128AENDS

CONTEXTSTRUCT
P1HomeQWORD?
P2HomeQWORD?
P3HomeQWORD?
P4HomeQWORD?
P5HomeQWORD?
P6HomeQWORD?
ContextFlagsDWORD?
MxCsrDWORD?
SegCsWORD?
SegDsWORD?
SegEsWORD?
SegFsWORD?
SegGsWORD?
SegSsWORD?
EFlagsDWORD?
_Dr0QWORD?
_Dr1QWORD?
_Dr2QWORD?
_Dr3QWORD?
_Dr6QWORD?
_Dr7QWORD?
_RaxQWORD?
_RcxQWORD?
_RdxQWORD?
_RbxQWORD?
_RspQWORD?
_RbpQWORD?
_RsiQWORD?
_RdiQWORD?
_R8QWORD?
_R9QWORD?
_R10QWORD?
_R11QWORD?
_R12QWORD?
_R13QWORD?
_R14QWORD?
_R15QWORD?
_RipQWORD?
; UNION XMM_SAVE_AREA32
HeaderM128A2DUP(<>)
LegacyM128A8DUP(<>)
_Xmm0M128A<>
_Xmm1M128A<>
_Xmm2M128A<>
_Xmm3M128A<>
_Xmm4M128A<>
_Xmm5M128A<>
_Xmm6M128A<>
_Xmm7M128A<>
_Xmm8M128A<>
_Xmm9M128A<>
_Xmm10M128A<>
_Xmm11M128A<>
_Xmm12M128A<>
_Xmm13M128A<>
_Xmm14M128A<>
_Xmm15M128A<>
; END OF UNION XMM_SAVE_AREA32

VectorRegisterM128A26DUP(<>)
VectorControlQWORD?
DebugControlQWORD?
LastBranchToRipQWORD?
LastBranchFromRipQWORD?
LastExceptionToRipQWORD?
LastExceptionFromRipQWORD?
CONTEXTENDS

goto_entrypoint PROC ; stack: QWORD, entrypoint: QWORD

mov rax, rdx ; entrypoint
mov rsp, rcx ; stack
push rax
xor rax, rax
xor rbx, rbx
xor rcx, rcx
xor rdx, rdx
xor rsi, rsi
xor rdi, rdi
xor rbp, rbp
xor r8, r8
xor r9, r9
xor r10, r10
xor r11, r11
xor r12, r12
xor r13, r13
xor r14, r14
xor r15, r15
ret

goto_entrypoint ENDP

restore_context PROC ; ctx: QWORD

mov rax, rcx ; ctx
mov rcx, [rax + CONTEXT._Rcx]
mov rdx, [rax + CONTEXT._Rdx]
mov rbx, [rax + CONTEXT._Rbx]
mov rsi, [rax + CONTEXT._Rsi]
mov rdi, [rax + CONTEXT._Rdi]
mov rsp, [rax + CONTEXT._Rsp]
mov rbp, [rax + CONTEXT._Rbp]
mov r8, [rax + CONTEXT._R8]
mov r9, [rax + CONTEXT._R9]
mov r10, [rax + CONTEXT._R10]
mov r11, [rax + CONTEXT._R11]
mov r12, [rax + CONTEXT._R12]
mov r13, [rax + CONTEXT._R13]
mov r14, [rax + CONTEXT._R14]
mov r15, [rax + CONTEXT._R15]
push [rax + CONTEXT._Rip]
mov rax, [rax + CONTEXT._Rax]
ret

restore_context ENDP

PUBLIC mm_check_read_begin, mm_check_read_end, mm_check_read_fail
mm_check_read PROC ; check_addr: QWORD, check_size: QWORD
xchg rcx, rdx
; rcx = check_size
; rdx = check_addr
jrcxz SUCC

mm_check_read_begin LABEL PTR
mov al, byte ptr [rdx]
; test first page which may be unaligned

mov rax, rdx
shr rax, 12
; rax - start page
lea rcx, [rdx + rcx - 1]
shr rcx, 12
; rcx - end page
sub rcx, rax
; rcx - remaining pages
je SUCC

and dx, 0f000h
L:
add rdx, 01000h
mov al, byte ptr [rdx]
loop L
mm_check_read_end LABEL PTR

SUCC:
xor rax, rax
inc eax
ret

mm_check_read_fail LABEL PTR
xor rax, rax
ret
mm_check_read ENDP

PUBLIC mm_check_read_string_begin, mm_check_read_string_end, mm_check_read_string_fail
mm_check_read_string PROC ; check_addr: QWORD
mov rdx, rcx ; check_addr

mm_check_read_string_begin LABEL PTR
L:
mov al, byte ptr [rdx]
test al, al
jz SUCC
inc rdx
mm_check_read_string_end LABEL PTR

SUCC:
xor rax, rax
inc eax
ret

mm_check_read_string_fail LABEL PTR
xor rax, rax
ret
mm_check_read_string ENDP

PUBLIC mm_check_write_begin, mm_check_write_end, mm_check_write_fail
mm_check_write PROC ; check_addr: QWORD, check_size: QWORD
xchg rcx, rdx
; rcx = check_size
; rdx = check_addr
jrcxz SUCC

mm_check_write_begin LABEL PTR
mov al, byte ptr [rdx]
mov byte ptr [rdx], al
; test first page which may be unaligned

mov rax, rdx
shr rax, 12
; rax - start page
lea rcx, [rdx + rcx - 1]
shr rcx, 12
; rcx - end page
sub rcx, rax
; rcx - remaining pages
je SUCC

and dx, 0f000h
L:
add rdx, 01000h
mov al, byte ptr [rdx]
mov byte ptr [rdx], al
loop L
mm_check_write_end LABEL PTR

SUCC:
xor rax, rax
inc eax
ret

mm_check_write_fail LABEL PTR
xor rax, rax
ret
mm_check_write ENDP

fpu_fxsave PROC ; save_area: QWORD (rcx = pointer to 16-byte aligned 512-byte save area)
fxsave64 [rcx]
ret
fpu_fxsave ENDP

fpu_fxrstor PROC ; save_area: QWORD (rcx = pointer to 16-byte aligned 512-byte save area)
fxrstor64 [rcx]
ret
fpu_fxrstor ENDP

OPTION PROLOGUE: NONE
OPTION EPILOGUE: NONE
; this function will be called by signal return path
; syscall 15 = rt_sigreturn on x86_64
signal_restorer PROC
mov eax, 15
syscall
signal_restorer ENDP

END
