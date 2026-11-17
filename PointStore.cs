namespace StationeersPlc
{
    /// <summary>The four classic PLC / Modbus data tables.</summary>
    internal enum Table
    {
        DiscreteInput = 0,   // read-only bits   (Modbus function 2)  - device status
        Coil = 1,            // read/write bits  (functions 1, 5, 15)  - device commands
        InputRegister = 2,   // read-only words  (function 4)          - device measurements
        HoldingRegister = 3  // read/write words (functions 3, 6, 16)  - device setpoints
    }

    /// <summary>A write that arrived over Modbus, waiting to be applied on the Unity main thread.</summary>
    internal struct WriteRequest
    {
        public Table Table;
        public int Address;
        public int Value;     // coil: 0 or 1; holding register: raw 16-bit value
    }

    /// <summary>
    /// The PLC-visible memory. Updated by the Unity main thread, read (and for outputs, written) by network threads.
    /// Single bool/short elements are read and written atomically, so no locking is needed.
    /// </summary>
    internal static class PointStore
    {
        /// <summary>Largest possible table. Modbus addresses are 16-bit, so this cannot go higher.</summary>
        public const int MaxSize = 65536;

        /// <summary>Addresses per table (0 .. Size-1). Set once at startup from the TableSize setting; see Init.</summary>
        public static int Size = MaxSize;

        public static bool[] Di = new bool[MaxSize];
        public static bool[] Coil = new bool[MaxSize];
        public static short[] Ir = new short[MaxSize];
        public static short[] Hr = new short[MaxSize];

        // Output addresses that belong to a signal. Writes outside these are refused with "illegal data address".
        public static bool[] CoilAssigned = new bool[MaxSize];
        public static bool[] HrAssigned = new bool[MaxSize];

        /// <summary>
        /// Resizes all tables. Call only during startup, before the servers and the driver exist (other threads read these
        /// arrays without locking). Calling it again with a bigger size later, while loading the I/O list, is safe for the
        /// same reason: nothing else is running yet.
        /// </summary>
        public static void Init(int size)
        {
            Size = size < 1 ? 1 : (size > MaxSize ? MaxSize : size);
            Di = new bool[Size];
            Coil = new bool[Size];
            Ir = new short[Size];
            Hr = new short[Size];
            CoilAssigned = new bool[Size];
            HrAssigned = new bool[Size];
        }
    }
}
