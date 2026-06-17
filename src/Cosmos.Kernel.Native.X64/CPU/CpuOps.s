.intel_syntax noprefix

.global _native_cpu_halt
.global _native_cpu_rdtsc
.global _native_cpu_disable_interrupts
.global _native_cpu_enable_interrupts
.global _native_cpu_save_irq_and_disable
.global _native_cpu_restore_irq
.global _native_cpu_cpuid
.global _native_cpu_rdmsr
.global _native_cpu_wrmsr

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


.equ CPUID__Eax,   0x00
.equ CPUID__Ebx,   0x04
.equ CPUID__Ecx,   0x08
.equ CPUID__Edx,   0x0C
// Read CPUID (eax, ecx, result*)
//
_native_cpu_cpuid:
push rbx
mov eax, edi
mov ecx, esi
mov rdi, rdx

cpuid

mov [rdi + CPUID__Eax], eax
mov [rdi + CPUID__Ebx], ebx
mov [rdi + CPUID__Ecx], ecx
mov [rdi + CPUID__Edx], edx

pop rbx
ret

// Read Model Specific Register
// Input: EDI = MSR index
// Returns: 64-bit MSR value in RAX
_native_cpu_rdmsr:
mov ecx, edi

rdmsr

// EDX:EAX contains the MSR value
shl rdx, 32
or rax, rdx     //High order bits are cleared by rdmsr, so we can just OR them in

ret

// Write Model Specific Register
// Input: EDI = MSR index, RSI = MSR value
// Returns: 64-bit MSR value in RAX
_native_cpu_wrmsr:
mov ecx, edi

mov eax, esi
mov rdx, rsi
shr rdx, 32

wrmsr

ret
