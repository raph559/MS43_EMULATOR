using Ms43Sim.Core;

namespace Ms43Sim.Tests.BoardDevices;

public class MonitoringModuleTests
{
    /// <summary>The CPU side: answers each byte with the next byte of the queued frames and records every call.</summary>
    sealed class FakeCpu : ISscPort
    {
        readonly Scheduler scheduler;
        readonly Queue<byte> pending = new();

        public FakeCpu(Scheduler scheduler) => this.scheduler = scheduler;

        public List<ulong> TransferCycles { get; } = new();

        /// <summary>Every byte the module clocked out, in order.</summary>
        public List<byte> FromModule { get; } = new();

        public void Queue(byte[] frame)
        {
            foreach (var b in frame)
                pending.Enqueue(b);
        }

        public ushort ExternalTransfer(ushort fromMaster)
        {
            TransferCycles.Add(scheduler.Now);
            FromModule.Add((byte)fromMaster);
            return pending.TryDequeue(out var b) ? b : (ushort)0xFF;
        }
    }

    sealed class H
    {
        bool selected;

        public H()
        {
            Cpu = new FakeCpu(Scheduler);
            Module = new MonitoringModule(Scheduler, Cpu, () => selected, () => 77, Log);
        }

        public Scheduler Scheduler { get; } = new();
        public EventLog Log { get; } = new();
        public FakeCpu Cpu { get; }
        public MonitoringModule Module { get; }

        public void Select(bool on)
        {
            selected = on;
            Module.ChipSelectChanged();
        }
    }

    static byte Xor(ReadOnlySpan<byte> bytes)
    {
        byte x = 0;
        foreach (var b in bytes)
            x ^= b;
        return x;
    }

    static byte[] WithXor(params int[] bytes)
    {
        var frame = bytes.Select(b => (byte)b).Append((byte)0).ToArray();
        frame[8] = Xor(frame.AsSpan(0, 8));
        return frame;
    }

    static byte[] Cpu12h() => WithXor(0x12, 0x32, 0, 0, 0, 0, 0, 0);

    static byte[] CpuCf() => WithXor(0xCF, 0, 0, 0, 0, 0, 0, 0);

    static byte[] CpuAdh(ushort answer) => WithXor(0xAD, answer >> 8, answer & 0xFF, 0, 0, 0, 0, 0);

    /// <summary>Selects, advances 10,000 cycles, deselects; returns the 9 bytes the module clocked.</summary>
    static byte[] Exchange(H h, byte[] cpuFrame)
    {
        h.Cpu.Queue(cpuFrame);
        var before = h.Cpu.FromModule.Count;
        h.Select(true);
        h.Scheduler.Advance(h.Scheduler.Now + 10_000);
        h.Select(false);
        return h.Cpu.FromModule.Skip(before).ToArray();
    }

    /// <summary>Exchanges 1-2, then n ADh exchanges that answer each question right; the module's n ADh frames.</summary>
    static List<byte[]> AdhFrames(H h, int n)
    {
        Exchange(h, Cpu12h());
        Exchange(h, CpuCf());
        var frames = new List<byte[]>();
        var question = 0;                                                 // the CFh frame counts as question 0
        for (var i = 0; i < n; i++)
        {
            var frame = Exchange(h, CpuAdh(MonitoringModule.ExpectedAnswer(question)));
            frames.Add(frame);
            question = frame[1];
        }
        return frames;
    }

    [Fact] public void Exchange1_Is12h_WithByte2Equal32h_AndItsXor()
    {
        var h = new H();
        var f = Exchange(h, Cpu12h());
        Assert.Equal((0x12, 0x32, 0x00, Xor(f[..8])), (f[0], f[2], f[5], f[8]));
    }

    [Fact] public void Exchange2_IsCf_WithTheChannel6Mirror()
    {
        var h = new H();
        Exchange(h, Cpu12h());
        Assert.Equal(new byte[] { 0xCF, 0, 0x00, 0, 77 >> 2, 0, 0, 0 }, Exchange(h, CpuCf())[..8]);
    }

    [Fact] public void AdhFrames_AskQuestion1First_AndWrapAfter27h()
    {
        var q = AdhFrames(new H(), 41).Select(f => (int)f[1]).ToArray();
        Assert.Equal((1, 0x27, 0x00, 0x01), (q[0], q[38], q[39], q[40]));
    }

    [Fact] public void ToggleByte_InvertsBits1To5_AtEvery8thAdhFrame()
    {
        var t = AdhFrames(new H(), 24).Select(f => (int)f[2]).ToArray();
        Assert.Equal((0x00, 0x00, 0x3E, 0x3E, 0x00), (t[0], t[6], t[7], t[14], t[15]));
    }

    [Fact] public void Counter_RisesAfterAWrongAnswer_AndFallsBack()
    {
        var h = new H();
        Exchange(h, Cpu12h()); Exchange(h, CpuCf());
        Exchange(h, CpuAdh(MonitoringModule.ExpectedAnswer(0)));        // right answer to question 0
        var during = Exchange(h, CpuAdh(MonitoringModule.Seed(1)));     // deliberate wrong answer to question 1
        var after = Exchange(h, CpuAdh(MonitoringModule.ExpectedAnswer(2)));
        Assert.Equal((0, 1, 0), ((int)during[3], (int)after[3], h.Module.ErrorCounter));
        Assert.Equal((1, 0), (h.Module.WrongAnswers, h.Module.UnexpectedAnswers));
    }

    [Fact] public void AnswerThatIsNeitherExpectedNorTheSeed_IsReported()
    {
        var h = new H();
        Exchange(h, Cpu12h()); Exchange(h, CpuCf()); Exchange(h, CpuAdh(0x1234));
        Assert.Equal(1, h.Module.UnexpectedAnswers);
        Assert.Contains("unexpected", Assert.Single(h.Log.All.OfType<MonitoringEvent>()).What);
    }

    [Fact] public void BadXor_AndUnexpectedType_AreReported()
    {
        var h = new H();
        var bad = Cpu12h();
        bad[8] ^= 1;
        Exchange(h, bad);
        Exchange(h, Cpu12h());                                            // exchange 2 wants CFh
        Assert.Equal(2, h.Module.UnexpectedAnswers);
        Assert.Equal(2, h.Log.All.OfType<MonitoringEvent>().Count());
    }

    [Fact] public void Timing_TheFirstByteEnds1248Cycles_AndTheLast8160_AfterTheFall()
    {
        var h = new H();
        var fall = h.Scheduler.Now;
        Exchange(h, Cpu12h());
        Assert.Equal((fall + 1_248, fall + 8_160), (h.Cpu.TransferCycles[0], h.Cpu.TransferCycles[8]));
    }

    [Fact] public void ChipSelectRisingEarly_AbortsTheExchange()
    {
        var h = new H();
        h.Cpu.Queue(Cpu12h());
        h.Select(true); h.Scheduler.Advance(h.Scheduler.Now + 2_000);   // byte 0 ends at 1,248, byte 1 at 2,112
        h.Select(false); h.Scheduler.Advance(h.Scheduler.Now + 20_000);
        Assert.Equal((0, 1), (h.Module.ExchangesSinceReset, h.Cpu.TransferCycles.Count));
    }

    [Fact] public void CpuReset_RestartsAt12h_AndHoldsTheShutOffAgain()
    {
        var h = new H();
        AdhFrames(h, 1);
        Assert.True(h.Module.ShutOffReleased);
        h.Module.OnCpuReset();
        Assert.False(h.Module.ShutOffReleased);
        Assert.Equal(0x12, Exchange(h, Cpu12h())[0]);
    }

    [Fact] public void Exchanges_CountsAcrossCpuResets()
    {
        var h = new H();
        AdhFrames(h, 1);
        h.Module.OnCpuReset();
        Exchange(h, Cpu12h());
        Assert.Equal((4, 1), (h.Module.Exchanges, h.Module.ExchangesSinceReset));
    }

    [Fact] public void Tables_MatchTheFirmware()
    {
        Assert.Equal((0xB246, 0x3E80), (MonitoringModule.ExpectedAnswer(0), MonitoringModule.Seed(0)));
        Assert.Equal((0x9BDD, 0xDFDB), (MonitoringModule.ExpectedAnswer(0x27), MonitoringModule.Seed(0x27)));
    }
}
