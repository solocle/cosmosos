using System.Runtime.InteropServices;

namespace Cosmos.Kernel.Core.X64.Bridge;

public struct CpuidResult
{
    public uint Eax;
    public uint Ebx;
    public uint Ecx;
    public uint Edx;
}

public static partial class X64CpuNative
{
    [LibraryImport("*", EntryPoint = "_native_cpu_rdtsc")]
    [SuppressGCTransition]
    public static partial ulong ReadTsc();

    [LibraryImport("*", EntryPoint = "_native_cpu_cpuid")]
    [SuppressGCTransition]
    public static unsafe partial void ReadCPUID(uint eax, uint ecx, CpuidResult* result);

    [LibraryImport("*", EntryPoint = "_native_cpu_rdmsr")]
    [SuppressGCTransition]
    public static unsafe partial ulong ReadMSR(uint index);

    [LibraryImport("*", EntryPoint = "_native_cpu_wrmsr")]
    [SuppressGCTransition]
    public static unsafe partial void WriteMSR(uint index, ulong value);
}
