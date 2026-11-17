using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace StationeersPlc
{
    /// <summary>
    /// Modbus TCP slave. Function codes 1, 2, 3, 4, 5, 6, 15, 16. Unit ID is ignored.
    /// Runs on its own threads and never touches Unity objects: it reads and writes PointStore,
    /// and queues accepted writes for the main thread.
    /// </summary>
    /// <remarks>
    /// Design notes:
    ///   * One accept thread plus one thread per client. Fine for a handful of PLC clients on loopback.
    ///   * Reads are served straight from PointStore (updated by the main thread).
    ///   * A write is validated here (address assigned, writes allowed), copied into PointStore immediately so a
    ///     read-back is consistent, and queued so the main thread can push it into the game.
    ///   * Exception codes: 1 illegal function (also used when writes are blocked), 2 illegal data address,
    ///     3 illegal data value, 4 server failure.
    ///   * There is no authentication, so the default bind address is 127.0.0.1.
    /// </remarks>
    internal sealed class ModbusServer
    {
        private TcpListener _listener;
        private Thread _accept;
        private volatile bool _running;
        private bool _allowWrites;

        public void Start(IPAddress ip, int port, bool allowWrites)
        {
            _allowWrites = allowWrites;
            _listener = new TcpListener(ip, port);
            _listener.Start();
            _running = true;
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "Modbus-accept" };
            _accept.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { break; }
                Interlocked.Increment(ref Stats.ClientsTotal);
                var t = new Thread(() => Serve(c)) { IsBackground = true, Name = "Modbus-client" };
                t.Start();
            }
        }

        private void Serve(TcpClient c)
        {
            Interlocked.Increment(ref Stats.ClientsActive);
            try
            {
                c.NoDelay = true;
                var s = c.GetStream();
                var hdr = new byte[7];
                while (_running)
                {
                    if (!ReadFull(s, hdr, 7)) break;
                    int len = (hdr[4] << 8) | hdr[5];
                    if (hdr[2] != 0 || hdr[3] != 0 || len < 2 || len > 254)
                    {
                        Interlocked.Increment(ref Stats.ProtocolErrors);
                        break;
                    }
                    var pdu = new byte[len - 1];
                    if (!ReadFull(s, pdu, pdu.Length)) break;
                    Interlocked.Increment(ref Stats.Requests);

                    byte[] rsp;
                    try { rsp = Handle(pdu); }
                    catch { Interlocked.Increment(ref Stats.ProtocolErrors); rsp = Ex(pdu[0], 4); }

                    var outp = new byte[7 + rsp.Length];
                    outp[0] = hdr[0]; outp[1] = hdr[1];
                    outp[4] = (byte)((rsp.Length + 1) >> 8);
                    outp[5] = (byte)((rsp.Length + 1) & 0xFF);
                    outp[6] = hdr[6];
                    Buffer.BlockCopy(rsp, 0, outp, 7, rsp.Length);
                    s.Write(outp, 0, outp.Length);
                }
            }
            catch { }
            finally
            {
                Interlocked.Decrement(ref Stats.ClientsActive);
                try { c.Close(); } catch { }
            }
        }

        private static bool ReadFull(NetworkStream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        private static byte[] Ex(byte fc, byte code) => new byte[] { (byte)(fc | 0x80), code };
        private static int U16(byte[] p, int i) => (p[i] << 8) | p[i + 1];

        private byte[] Handle(byte[] p)
        {
            byte fc = p[0];
            switch (fc)
            {
                case 1: return ReadBits(p, PointStore.Coil);
                case 2: return ReadBits(p, PointStore.Di);
                case 3: return ReadRegs(p, PointStore.Hr);
                case 4: return ReadRegs(p, PointStore.Ir);
                case 5: return WriteSingleCoil(p);
                case 6: return WriteSingleReg(p);
                case 15: return WriteCoils(p);
                case 16: return WriteRegs(p);
                default: return Ex(fc, 1);
            }
        }

        private static byte[] ReadBits(byte[] p, bool[] src)
        {
            if (p.Length < 5) return Ex(p[0], 3);
            int addr = U16(p, 1), n = U16(p, 3);
            if (n < 1 || n > 2000) return Ex(p[0], 3);
            if (addr + n > PointStore.Size) return Ex(p[0], 2);
            Interlocked.Increment(ref Stats.Reads);
            int bytes = (n + 7) / 8;
            var r = new byte[2 + bytes];
            r[0] = p[0]; r[1] = (byte)bytes;
            for (int i = 0; i < n; i++)
                if (src[addr + i]) r[2 + i / 8] |= (byte)(1 << (i % 8));
            return r;
        }

        private static byte[] ReadRegs(byte[] p, short[] src)
        {
            if (p.Length < 5) return Ex(p[0], 3);
            int addr = U16(p, 1), n = U16(p, 3);
            if (n < 1 || n > 125) return Ex(p[0], 3);
            if (addr + n > PointStore.Size) return Ex(p[0], 2);
            Interlocked.Increment(ref Stats.Reads);
            var r = new byte[2 + n * 2];
            r[0] = p[0]; r[1] = (byte)(n * 2);
            for (int i = 0; i < n; i++)
            {
                short v = src[addr + i];
                r[2 + i * 2] = (byte)(v >> 8);
                r[3 + i * 2] = (byte)(v & 0xFF);
            }
            return r;
        }

        // The in-game ST program owns the outputs while it runs, unless the player turns that off in the config.
        private bool WritesBlocked => !_allowWrites || (StRuntime.Running && StRuntime.BlockExternalWrites);

        private byte[] Refuse(byte fc, bool unassigned)
        {
            if (unassigned) Interlocked.Increment(ref Stats.UnassignedWrites);
            return Ex(fc, unassigned ? (byte)2 : (byte)1);
        }

        private static void Queue(Table t, int addr, int value)
        {
            Plugin.PendingWrites.Enqueue(new WriteRequest { Table = t, Address = addr, Value = value });
            Interlocked.Increment(ref Stats.WritesQueued);
        }

        private byte[] WriteSingleCoil(byte[] p)
        {
            if (p.Length < 5) return Ex(5, 3);
            int addr = U16(p, 1), raw = U16(p, 3);
            if (raw != 0x0000 && raw != 0xFF00) return Ex(5, 3);
            if (WritesBlocked) return Refuse(5, false);
            if (addr >= PointStore.Size || !PointStore.CoilAssigned[addr]) return Refuse(5, true);
            bool on = raw == 0xFF00;
            PointStore.Coil[addr] = on;
            Queue(Table.Coil, addr, on ? 1 : 0);
            return (byte[])p.Clone();
        }

        private byte[] WriteSingleReg(byte[] p)
        {
            if (p.Length < 5) return Ex(6, 3);
            int addr = U16(p, 1);
            short v = (short)U16(p, 3);
            if (WritesBlocked) return Refuse(6, false);
            if (addr >= PointStore.Size || !PointStore.HrAssigned[addr]) return Refuse(6, true);
            PointStore.Hr[addr] = v;
            Queue(Table.HoldingRegister, addr, v);
            return (byte[])p.Clone();
        }

        private byte[] WriteCoils(byte[] p)
        {
            if (p.Length < 6) return Ex(15, 3);
            int addr = U16(p, 1), n = U16(p, 3), bc = p[5];
            if (n < 1 || n > 1968 || bc != (n + 7) / 8 || p.Length < 6 + bc) return Ex(15, 3);
            if (addr + n > PointStore.Size) return Ex(15, 2);
            if (WritesBlocked) return Refuse(15, false);
            for (int i = 0; i < n; i++)
                if (!PointStore.CoilAssigned[addr + i]) return Refuse(15, true);
            for (int i = 0; i < n; i++)
            {
                bool on = (p[6 + i / 8] & (1 << (i % 8))) != 0;
                PointStore.Coil[addr + i] = on;
                Queue(Table.Coil, addr + i, on ? 1 : 0);
            }
            return new byte[] { 15, p[1], p[2], p[3], p[4] };
        }

        private byte[] WriteRegs(byte[] p)
        {
            if (p.Length < 6) return Ex(16, 3);
            int addr = U16(p, 1), n = U16(p, 3), bc = p[5];
            if (n < 1 || n > 123 || bc != n * 2 || p.Length < 6 + bc) return Ex(16, 3);
            if (addr + n > PointStore.Size) return Ex(16, 2);
            if (WritesBlocked) return Refuse(16, false);
            for (int i = 0; i < n; i++)
                if (!PointStore.HrAssigned[addr + i]) return Refuse(16, true);
            for (int i = 0; i < n; i++)
            {
                short v = (short)U16(p, 6 + i * 2);
                PointStore.Hr[addr + i] = v;
                Queue(Table.HoldingRegister, addr + i, v);
            }
            return new byte[] { 16, p[1], p[2], p[3], p[4] };
        }
    }
}
