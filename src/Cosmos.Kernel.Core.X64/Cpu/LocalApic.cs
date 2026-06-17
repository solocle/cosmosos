// This code is licensed under MIT license (see LICENSE for details)

using System.Runtime.CompilerServices;
using Cosmos.Kernel.Core;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.IO;
using Cosmos.Kernel.Core.Scheduler;

namespace Cosmos.Kernel.Core.X64.Cpu;

/// <summary>
/// Local APIC (Advanced Programmable Interrupt Controller) driver.
/// Each CPU has its own Local APIC for receiving interrupts.
/// </summary>
public static class LocalApic
{
    // Local APIC implementations
    private abstract class LocalApicImpl
    {
        public abstract uint Read(LAPIC_REGISTER register);
        public abstract void Write(LAPIC_REGISTER register, uint value);
        public abstract void Write64(LAPIC_REGISTER register, ulong value);

        public abstract ulong BaseAddress { get; }

        public abstract uint ReadId();
    }

    private class xAPIC : LocalApicImpl
    {
        private static ulong _baseAddress;
        public override ulong BaseAddress => _baseAddress;
        public xAPIC(ulong baseAddress)
        {
            _baseAddress = baseAddress;
        }

        private static ulong Address(LAPIC_REGISTER register)
        {
            return _baseAddress + (uint)register * 0x10;
        }

        public override uint Read(LAPIC_REGISTER register)
        {
            return Native.MMIO.Read32(Address(register));
        }

        public override void Write(LAPIC_REGISTER register, uint value)
        {
            Native.MMIO.Write32(Address(register), value);
        }
        public override void Write64(LAPIC_REGISTER register, ulong value)
        {
            //Write high, then low - this is used for the ICR
            Write(register + 1, (uint)(value >> 32));
            Write(register, (uint)value);
        }

        public override uint ReadId() => Read(LAPIC_REGISTER.ID) >> 24;
    }

    private class  x2APIC : LocalApicImpl
    {
        private const uint IA32_X2APIC_BASE = 0x800; // Base MSR address
        public override ulong BaseAddress => IA32_X2APIC_BASE;
        public x2APIC()
        {
            // x2APIC uses MSRs for register access, no base address needed
        }

        private static uint Address(LAPIC_REGISTER register)
        {
            return IA32_X2APIC_BASE + (uint)register;
        }

        public override uint Read(LAPIC_REGISTER register) => (uint)X64CpuOps.ReadMSR(Address(register));
        public override void Write(LAPIC_REGISTER register, uint value) => X64CpuOps.WriteMSR(Address(register), value);
        public override void Write64(LAPIC_REGISTER register, ulong value) => X64CpuOps.WriteMSR(Address(register), value);

        public override uint ReadId() => Read(LAPIC_REGISTER.ID);
    }
    // Local APIC register offsets (from base address)
    private enum LAPIC_REGISTER : uint
    {
        ID = 0x2,
        VERSION = 0x3,
        TPR = 0x8,
        PPR = 0xA,
        EOI = 0xB,
        LDR = 0xD,
        SVR = 0xF,
        ISR_BASE = 0x10,
        TMR_BASE = 0x18,
        IRR_BASE = 0x20,
        ESR = 0x28,
        ICR = 0x30,
        ICR_LOW = 0x30,     //Architectural difference between xAPIC and x2APIC, for xAPIC, ICR is split into two registers: ICR_LOW and ICR_HIGH
        ICR_HIGH = 0x31,
        TIMER_LVT = 0x32,
        THERMAL_LVT = 0x33,
        PERF_LVT = 0x34,
        LINT0 = 0x35,
        LINT1 = 0x36,
        ERROR_LVT = 0x37,
        TIMER_INIT = 0x38,
        TIMER_CURRENT = 0x39,
        TIMER_DIVIDE = 0x3E,
        SELF_IPI = 0x3F,
    }

    private const uint IA32_APIC_BASE = 0x1B; // MSR for APIC base address

    private const uint IA32_APIC_ENABLE = 1 << 11; // Enable xAPIC
    private const uint IA32_X2APIC_ENABLE = 1 << 10; // Enable x2APIC
    private const uint IA32_APIC_BSP = 1 << 8; // Processor is BSP

    // Timer LVT bits
    private const uint TIMER_MASKED = 0x10000;    // Timer interrupt masked
    private const uint TIMER_PERIODIC = 0x20000;  // Periodic mode (vs one-shot)
    public const byte TIMER_VECTOR = 0xEF;        // Timer interrupt vector (239) - separate from IRQ remapping range

    // Timer divide values (divide by 1, 2, 4, 8, 16, 32, 64, 128)
    private const uint TIMER_DIVIDE_BY_1 = 0xB;
    private const uint TIMER_DIVIDE_BY_16 = 0x3;

    // PIT ports for calibration
    private const ushort PIT_CHANNEL0_DATA = 0x40;
    private const ushort PIT_COMMAND = 0x43;
    private const uint PIT_FREQUENCY = 1193182;   // PIT base frequency in Hz

    // SVR bits
    private const uint SVR_ENABLE = 0x100;        // APIC Software Enable
    private const byte SPURIOUS_VECTOR = 0xFF;    // Spurious interrupt vector

    private static LocalApicImpl _implementation = null!;

    private static bool _initialized;
    private static uint _ticksPerMs;
    private static bool _timerCalibrated;
    private static uint _timerIntervalMs;
    private static ulong _timerIntervalNs;

    /// <summary>
    /// Gets the base address of the Local APIC.
    /// </summary>
    public static ulong BaseAddress => _implementation.BaseAddress;

    /// <summary>
    /// Gets whether the Local APIC is initialized.
    /// </summary>
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Initializes the Local APIC with the given base address from MADT.
    /// </summary>
    /// <param name="baseAddress">The physical address of the Local APIC registers.</param>
    public static void Initialize(ulong baseAddress)
    {
        //Check if X2APIC is present
        X64CpuOps.GetCPUID(1U, 0U, out Bridge.CpuidResult cpuidInfo);
        bool hasx2apic = (cpuidInfo.Ecx & (1 << 21)) != 0;

        if(hasx2apic)
        {
            _implementation = new x2APIC();
        }
        else
        {
            _implementation = new xAPIC(baseAddress);
        }
        ulong apicmsr = X64CpuOps.ReadMSR(IA32_APIC_BASE); // Ensure APIC is enabled in MSR
        apicmsr |= IA32_APIC_ENABLE; // Enable xAPIC
        X64CpuOps.WriteMSR(IA32_APIC_BASE, apicmsr);
        if (hasx2apic)
        {
            apicmsr |= IA32_X2APIC_ENABLE; // Enable x2APIC
            X64CpuOps.WriteMSR(IA32_APIC_BASE, apicmsr);
        }


        Serial.Write("[LocalAPIC] Initializing at 0x", baseAddress.ToString("X"), "\n");

        // Read APIC ID and version for diagnostics
        uint id = _implementation.ReadId();
        uint version = Read(LAPIC_REGISTER.VERSION);

        Serial.Write("[LocalAPIC] ID: ", id, "\n");
        Serial.Write("[LocalAPIC] Version: 0x", version.ToString("X"), "\n");

        // Clear error status register (requires two writes)
        Write(LAPIC_REGISTER.ESR, 0);
        Write(LAPIC_REGISTER.ESR, 0);

        // Set Task Priority to 0 to accept all interrupts
        Write(LAPIC_REGISTER.TPR, 0);

        // Configure spurious interrupt vector and enable APIC
        uint svr = SPURIOUS_VECTOR | SVR_ENABLE;
        Write(LAPIC_REGISTER.SVR, svr);

        Serial.Write("[LocalAPIC] SVR set to 0x", svr.ToString("X"), "\n");

        // Mask LINT0 and LINT1 (we use I/O APIC for external interrupts)
        Write(LAPIC_REGISTER.LINT0, TIMER_MASKED);  // Masked
        Write(LAPIC_REGISTER.LINT1, TIMER_MASKED);  // Masked

        // Mask timer and error LVT entries
        Write(LAPIC_REGISTER.TIMER_LVT, TIMER_MASKED);  // Masked
        Write(LAPIC_REGISTER.ERROR_LVT, TIMER_MASKED);  // Masked

        _initialized = true;
        Serial.Write("[LocalAPIC] Initialization complete\n");
    }

    /// <summary>
    /// Sends End of Interrupt signal to the Local APIC.
    /// Must be called at the end of every interrupt handler.
    /// </summary>
    public static void SendEOI()
    {
        if (_initialized)
        {
            Write(LAPIC_REGISTER.EOI, 0);
        }
    }

    /// <summary>
    /// Reads the In-Service Register to check which interrupts are being serviced.
    /// Returns the ISR value for vectors 32-63 (ISR1).
    /// </summary>
    public static uint GetISR(byte offset)
    {
        return _implementation.Read(LAPIC_REGISTER.ISR_BASE + offset);
    }
    public static uint GetISR1() => GetISR(1);

    /// <summary>
    /// Reads the Interrupt Request Register to check pending interrupts.
    /// Returns the IRR value for vectors 32-63 (IRR1).
    /// </summary>
    public static uint GetIRR(byte offset)
    {
        return _implementation.Read(LAPIC_REGISTER.IRR_BASE + offset);
    }
    public static uint GetIRR1() => GetIRR(1);
    /// <summary>
    /// Gets the current Local APIC ID.
    /// </summary>
    public static uint GetId() => _implementation.ReadId();

    private static uint Read(LAPIC_REGISTER register) => _implementation.Read(register);
    private static void Write(LAPIC_REGISTER register, uint value) => _implementation.Write(register, value);
    private static void Write64(LAPIC_REGISTER register, ulong value) => _implementation.Write64(register, value);

    /// <summary>
    /// Gets whether the LAPIC timer has been calibrated.
    /// </summary>
    public static bool IsTimerCalibrated => _timerCalibrated;

    /// <summary>
    /// Gets the calibrated ticks per millisecond.
    /// </summary>
    public static uint TicksPerMs => _ticksPerMs;

    /// <summary>
    /// Gets the current timer interval in nanoseconds.
    /// </summary>
    public static ulong TimerIntervalNs => _timerIntervalNs;

    /// <summary>
    /// Calibrates the LAPIC timer using the PIT as a reference.
    /// Must be called after Initialize() and before using timer functions.
    /// </summary>
    public static void CalibrateTimer()
    {
        if (!_initialized)
        {
            Serial.Write("[LocalAPIC] ERROR: Cannot calibrate timer - APIC not initialized\n");
            return;
        }

        Serial.Write("[LocalAPIC] Calibrating timer using PIT...\n");

        // Set timer divide to 16
        Write(LAPIC_REGISTER.TIMER_DIVIDE, TIMER_DIVIDE_BY_16);

        // Configure PIT channel 0 for one-shot mode, ~10ms delay
        // 10ms = 11932 ticks at 1193182 Hz
        const ushort pitCount = 11932;

        // PIT command: channel 0, lobyte/hibyte, one-shot mode, binary
        Native.IO.Write8(PIT_COMMAND, 0x30);
        Native.IO.Write8(PIT_CHANNEL0_DATA, (byte)(pitCount & 0xFF));
        Native.IO.Write8(PIT_CHANNEL0_DATA, (byte)(pitCount >> 8));

        // Set LAPIC timer to max initial count (one-shot, masked)
        Write(LAPIC_REGISTER.TIMER_LVT, TIMER_MASKED);
        Write(LAPIC_REGISTER.TIMER_INIT, 0xFFFFFFFF);

        // Wait for PIT to count down by polling
        // Read back current count until it wraps or reaches near zero
        ushort lastCount = pitCount;
        while (true)
        {
            // Latch count for channel 0
            Native.IO.Write8(PIT_COMMAND, 0x00);
            byte lo = Native.IO.Read8(PIT_CHANNEL0_DATA);
            byte hi = Native.IO.Read8(PIT_CHANNEL0_DATA);
            ushort currentCount = (ushort)(lo | (hi << 8));

            // PIT counts down, check if we've passed our target
            if (currentCount > lastCount || currentCount == 0)
            {
                break;
            }

            lastCount = currentCount;
        }

        // Read how many LAPIC ticks elapsed
        uint lapicTicksElapsed = 0xFFFFFFFF - Read(LAPIC_REGISTER.TIMER_CURRENT);

        // Stop the timer
        Write(LAPIC_REGISTER.TIMER_INIT, 0);

        // Calculate ticks per ms (we waited ~10ms)
        // Account for divide by 16
        _ticksPerMs = lapicTicksElapsed / 10;

        _timerCalibrated = true;
        Serial.Write("[LocalAPIC] Timer calibrated: ", _ticksPerMs, " ticks/ms\n");
    }

    /// <summary>
    /// Blocks for the specified number of milliseconds using the LAPIC timer.
    /// </summary>
    /// <param name="ms">Number of milliseconds to wait.</param>
    public static void Wait(uint ms)
    {
        if (!_timerCalibrated)
        {
            Serial.Write("[LocalAPIC] ERROR: Timer not calibrated\n");
            return;
        }

        if (ms == 0)
        {
            return;
        }

        // Set timer divide to 16 (same as calibration)
        Write(LAPIC_REGISTER.TIMER_DIVIDE, TIMER_DIVIDE_BY_16);

        // Calculate ticks needed
        uint ticks = _ticksPerMs * ms;

        // Set up one-shot timer (masked - we poll instead of interrupt)
        Write(LAPIC_REGISTER.TIMER_LVT, TIMER_MASKED);
        Write(LAPIC_REGISTER.TIMER_INIT, ticks);

        // Poll until timer reaches zero
        while (Read(LAPIC_REGISTER.TIMER_CURRENT) > 0)
        {
            // Busy wait
        }
    }

    /// <summary>
    /// Starts the LAPIC timer in periodic mode with the given interval.
    /// Will fire interrupt on TIMER_VECTOR (0xEF = 239).
    /// </summary>
    /// <param name="intervalMs">Interval in milliseconds between interrupts.</param>
    public static void StartPeriodicTimer(uint intervalMs)
    {
        if (!_timerCalibrated)
        {
            Serial.Write("[LocalAPIC] ERROR: Timer not calibrated\n");
            return;
        }

        uint ticks = _ticksPerMs * intervalMs;
        _timerIntervalMs = intervalMs;
        _timerIntervalNs = (ulong)intervalMs * 1_000_000UL;  // Convert ms to ns

        Serial.Write("[LocalAPIC] Starting periodic timer:\n");
        Serial.Write("[LocalAPIC]   TicksPerMs: ", _ticksPerMs, "\n");
        Serial.Write("[LocalAPIC]   Interval: ", intervalMs, "ms\n");
        Serial.Write("[LocalAPIC]   Ticks: ", ticks, "\n");
        Serial.Write("[LocalAPIC]   Vector: 0x", TIMER_VECTOR.ToString("X"), "\n");

        // Set timer divide to 16
        Write(LAPIC_REGISTER.TIMER_DIVIDE, TIMER_DIVIDE_BY_16);

        // Configure timer: periodic mode, unmasked, vector 0xEF
        Write(LAPIC_REGISTER.TIMER_LVT, TIMER_PERIODIC | TIMER_VECTOR);

        // Set initial count to start the timer
        Write(LAPIC_REGISTER.TIMER_INIT, ticks);
    }

    /// <summary>
    /// Stops the LAPIC timer.
    /// </summary>
    public static void StopTimer()
    {
        // Mask the timer and set count to 0
        Write(LAPIC_REGISTER.TIMER_LVT, TIMER_MASKED);
        Write(LAPIC_REGISTER.TIMER_INIT, 0);
    }

    /// <summary>
    /// Registers the LAPIC timer interrupt handler.
    /// Should be called after InterruptManager is initialized.
    /// </summary>
    public static void RegisterTimerHandler()
    {
        Serial.Write("[LocalAPIC] Registering timer handler for vector 0x", TIMER_VECTOR.ToString("X"), "\n");
        InterruptManager.SetHandler(TIMER_VECTOR, HandleTimerInterrupt);
    }

    /// <summary>
    /// LAPIC timer interrupt handler - triggers scheduler for preemptive multitasking.
    /// </summary>
    private static uint _timerTickCount;
    private static unsafe void HandleTimerInterrupt(ref IRQContext context)
    {
        _timerTickCount++;

        // Get current CPU ID from APIC
        uint cpuId = GetId();

        // Calculate RSP pointing to saved context for context switching
        nuint contextPtr = (nuint)Unsafe.AsPointer(ref context);
        nuint currentRsp = contextPtr - 256;  // RSP points to start of XMM save area

        // Sanity check RSP - should be in kernel space (0xFFFF800000000000+)
        if ((currentRsp & 0xFFFF000000000000) != 0xFFFF000000000000)
        {
            Serial.Write("[LAPIC] ERROR: Invalid RSP at tick ", _timerTickCount, ": ");
            Serial.WriteHex((ulong)currentRsp);
            Serial.Write("\n");
            return;  // Don't call scheduler with bad RSP
        }

        // Log first few and then periodically
        if (_timerTickCount <= 5 || _timerTickCount % 100 == 0)
        {
            Serial.Write("[LAPIC] Timer tick ", _timerTickCount, " RSP=");
            Serial.WriteHex((ulong)currentRsp);
            Serial.Write("\n");
        }

        // Call scheduler with elapsed time
        SchedulerManager.OnTimerInterrupt(cpuId, currentRsp, _timerIntervalNs);

        // EOI is sent by InterruptManager.Dispatch after handler returns
    }
}
