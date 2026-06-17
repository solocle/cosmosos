.intel_syntax noprefix

.global _native_cpu_halt
.global _native_cpu_rdtsc
.global _native_cpu_disable_interrupts
.global _native_cpu_enable_interrupts
.global _native_cpu_save_irq_and_disable
.global _native_cpu_restore_irq
.global _native_cpu_cpuid

.text

_native_cpu_halt:
    hlt
    ret

// Disable interrupts (CLI)
_native_cpu_disable_interrupts:
    cli
    ret

// Enable interrupts (STI)
_native_cpu_enable_interrupts:
    sti
    ret

// Save RFLAGS and disable interrupts.
// Returns: previous RFLAGS in RAX (System V ABI).
_native_cpu_save_irq_and_disable:
    pushfq
    pop     rax
    cli
    ret

// Restore RFLAGS from first argument (RDI on System V ABI).
_native_cpu_restore_irq:
    push    rdi
    popfq
    ret

// Read Time Stamp Counter
// Returns: 64-bit TSC value in RAX
_native_cpu_rdtsc:
    rdtsc                   // EDX:EAX = timestamp counter
    shl     rdx, 32         // Shift high 32 bits to upper half of RDX
    or      rax, rdx        // Combine into RAX (return value)
    ret


.equ CPUID__Rax,   0x00
.equ CPUID__Rbx,   0x08
.equ CPUID__Rcx,   0x10
.equ CPUID__Rdx,   0x18
// Read CPUID (eax, ecx, result*)
//
_native_cpu_cpuid:
push rbx
mov eax, edi
mov ecx, esi
mov rdi, rdx

cpuid

mov [rdi + CPUID__Rax], rax
mov [rdi + CPUID__Rbx], rbx
mov [rdi + CPUID__Rcx], rcx
mov [rdi + CPUID__Rdx], rdx

pop rbx
ret
