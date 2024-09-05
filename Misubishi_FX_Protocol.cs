using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.IO.Ports;
using System.Windows.Forms;
using System.Resources;
using static System.Windows.Forms.AxHost;
using System.Net.Security;
using System.Diagnostics;
using System.Drawing.Text;
using System.Drawing;
using System.Linq.Expressions;

namespace Mitsubishi_FX
{
    /// <summary>
    /// Trasnfer layer generic interface to be used with the FX class
    /// </summary>
    public interface ItransLayer
    {

        /// <summary>
        /// Should return the number of available byes to read
        /// </summary>
        /// <returns></returns>
        int GetAvailable();

        /// <summary>
        /// Writes a number of bytes to the interface
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns>the actual number of bytes writtedn, -ve value indicates error</returns>
        int Write(byte[] buffer, int offset, int count);

        /// <summary>
        /// Reads data from the interface
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns>number of bytes read, -ve value indicates error</returns>
        int Read(byte[] buffer, int offset, int count);

        /// <summary>
        /// Clears the output buffer
        /// </summary>
        bool FlushOutputBuffer();

        /// <summary>
        /// clears the input buffer
        /// </summary>
        bool FlushInputBuffer();

        /// <summary>
        /// Opens the communication channel
        /// </summary>
        /// <returns>true is open</returns>
        bool Open();

        /// <summary>
        /// Closes the communication channel
        /// </summary>
        /// <returns>true if corectly closed</returns>
        bool Close();

        /// <summary>
        /// Returns true if it's connected
        /// </summary>
        /// <returns></returns>
        bool IsConnected();
    }

    /// <summary>
    /// Serial port transfer layer implmentation
    /// can be sued as a reference to implement more transfer layers (over TCP or Wifi for ex)
    /// </summary>
    public class MFX_SerialTP : ItransLayer
    {
        SerialPort _port;
        public MFX_SerialTP(string PortName, int BaudRate, Parity parity, StopBits stops, int DataBits, int ReadTimeout)
        {
            _port = new SerialPort();
            _port.PortName = PortName;
            _port.BaudRate = BaudRate;
            _port.Parity = parity;
            _port.DataBits = DataBits;
            _port.StopBits = stops;
            _port.ReadTimeout = ReadTimeout;
        }
        public int GetAvailable()
        {
            return _port.BytesToRead;
        }

        public int Write(byte[] buffer, int offset, int count)
        {
            try
            {
                _port.Write(buffer, offset, count);
                System.Threading.Thread.Sleep(15);
                return count;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return -1;
            }
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            int len = 0;
            try
            {
                long L = Environment.TickCount + _port.ReadTimeout;
                while (Environment.TickCount < L)
                {
                    if (_port.BytesToRead >= count)
                    {
                        len = _port.Read(buffer, offset, count);
                        return len;
                    }
                    System.Threading.Thread.Sleep(5);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                len = -1;
            }
            return len;
        }

        public bool FlushOutputBuffer()
        {
            try
            {
                _port.DiscardOutBuffer();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }
        public bool FlushInputBuffer()
        {
            try
            {
                if (_port.BytesToRead > 0)
                {
                    _port.ReadExisting();
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }
        public bool Open()
        {
            try
            {
                _port.Open();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }
        public bool Close()
        {
            try
            {
                _port.Close();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }
        public bool IsConnected()
        {
            return _port.IsOpen;
        }
    }

    public class MFX_Protocol
    {
        static byte[] BitMasks = new byte[] { 1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80 };

        //used for clearing
        static byte[] NbitMasks = new byte[] { 0xfe, 0xfd, 0xfb, 0xf7, 0xef, 0xdf, 0xbf, 0x7f };

        private readonly object lockObj = new object();
        /// <summary>
        /// Tranport and Physical layer interface (supports multiple archs)
        /// </summary>
        public ItransLayer _Interface;

        //Buffersused for transactions
        //transfers alre limited to 64 bytes (each byte takes 2 bytes in ASCII) so a min buffer size needed is 128 + STX + ETX + Chk1 + CHK2 =132 bytes
        private byte[] TxBuffer = new byte[140];
        private byte[] RxBuffer = new byte[140];

        private int RespLen = 0;
        private int ReqLen = 0;

        int DecAddress = 0; //data address in Decimal format
        int FirstByteAddress = 0, LastByteAddress = 0;
        int AbsDevAddress = 0; //holds the device address to be sent to the CPu
        private ushort BytesToRead;


        public MFX_Protocol(ItransLayer Interface)
        {
            this._Interface = Interface;
        }

        public bool Start()
        {
            return _Interface.Open();
        }


        /// <summary>
        /// Reads bit data from the CPU
        /// </summary>
        /// <param name="BitType">memory type</param>
        /// <param name="StartingAddress">address of the bit memory to start at</param>
        /// <param name="len">length of data to be read each with its relevant type</param>
        /// <param name="Data">location where the read data is stored</param>
        /// <returns>status of the transaction</returns>
        public MFX_State ReadBitData(RegisterType BitType, int StartingAddress, int len, out bool[] ReadData)
        {
            lock (lockObj)
            {
                ReadData = new bool[len];
                MFX_State state = MFX_State.Processing;

                ReqLen = 0;

                state = CheckAddressAndLength(BitType, StartingAddress, len);

                if (state != MFX_State.OK) { return state; }

                //only allow bit data
                if (BitType > RegisterType.M_Special)
                {
                    state = MFX_State.Error_Incorrect_Data_Type; return state;
                }

                TxBuffer[ReqLen++] = STX;
                TxBuffer[ReqLen++] = 0x30;

                var t = Encoding.UTF8.GetBytes($"{AbsDevAddress:X4}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];
                TxBuffer[ReqLen++] = t[2];
                TxBuffer[ReqLen++] = t[3];

                t = Encoding.UTF8.GetBytes($"{BytesToRead:X2}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                TxBuffer[ReqLen++] = ETX;

                t = MFX_Protocol.ComputeChecksum(TxBuffer, 1, ReqLen - 1);

                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                //      STX + ETX + CHK
                RespLen = 4 + (BytesToRead * 2);

                if (!_Interface.FlushInputBuffer()) { state = MFX_State.Error_TX; return state; }
                if (_Interface.Write(TxBuffer, 0, ReqLen) != ReqLen) { state = MFX_State.Error_TX; return state; }

                state = AwaitCheckResponse();

                if (state == MFX_State.OK)
                {
                    byte[] Data = ParseData(RxBuffer, 1, BytesToRead);
                    for (int x = DecAddress; x < len + DecAddress; x++)
                    {
                        ReadData[x] = (Data[x / 8] & BitMasks[x % 8]) > 0 ? true : false;
                    }
                }
                return state;
            }
        }

        /// <summary>
        /// Writes bit data to the CPU i the form of bytes
        /// Care should be taken not to try to write any data out of alignment
        /// for ex. cannot start writing data starting from y1, you need to start at y0
        /// Warning!! outputs override is not working on the chiese version
        /// </summary>
        /// <param name="BitType">memory type</param>
        /// <param name="StartingAddress">Base address of the bit memory to start at</param>
        /// <param name="len">length of data to be written in bytes</param>
        /// <param name="Data">Content to be written</param>
        /// <returns></returns>
        public MFX_State WriteBitData(RegisterType BitType, ushort StartingAddress, byte len, bool[] Data)
        {
            lock (lockObj)
            {
                MFX_State state = MFX_State.Processing;

                ReqLen = 0;

                state = CheckAddressAndLength(BitType, StartingAddress, len);
                if (state != MFX_State.OK) { return state; }

                if (BitType > RegisterType.M_Special) { state = MFX_State.Error_Incorrect_Data_Type; return state; }

                //check for correct alignment
                if ((DecAddress % 8) > 0)
                {
                    state = MFX_State.Error_Address_Incorrectly_Aligned; return state;
                }

                TxBuffer[ReqLen++] = STX;
                TxBuffer[ReqLen++] = 0x31; //write data

                var t = Encoding.UTF8.GetBytes($"{AbsDevAddress:X4}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];
                TxBuffer[ReqLen++] = t[2];
                TxBuffer[ReqLen++] = t[3];

                t = Encoding.UTF8.GetBytes($"{len:X2}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                byte[] EncData = new byte[len];
                //load data into the TX buffer
                for (int x = 0; x < (len * 8); x++)
                {
                    EncData[x / 8] |= (Data[x] == true) ? BitMasks[x % 8] : (byte)0;
                }
                EncodeData(TxBuffer, EncData, ReqLen, EncData.Length);

                ReqLen += (len * 2);

                TxBuffer[ReqLen++] = ETX;

                t = MFX_Protocol.ComputeChecksum(TxBuffer, 1, ReqLen - 1);

                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                //        ACK or NACK
                RespLen = 1;

                if (!_Interface.FlushInputBuffer()) { state = MFX_State.Error_TX; return state; }
                if (_Interface.Write(TxBuffer, 0, ReqLen) != ReqLen) { state = MFX_State.Error_TX; return state; }

                state = AwaitCheckResponse();

                return state;
            }
        }

        /// <summary>
        /// Forces a bit device to a certain logic level
        /// </summary>
        /// <param name="BitType">allowed types are bit device types</param>
        /// <param name="BitAddress">addressof the bit device</param>
        /// <param name="Status">required status</param>
        /// <returns></returns>
        public MFX_State Force_Bit(RegisterType BitType, ushort BitAddress, bool Status)
        {
            ReqLen = 0;
            MFX_State state = MFX_State.OK;

            state = CheckForcedAddres(BitType, BitAddress);

            if (state != MFX_State.OK) { return state; }

            TxBuffer[ReqLen++] = STX;
            TxBuffer[ReqLen++] = (Status == true) ? (byte)0x37 : (byte)0x38; //write data

            var t = Encoding.UTF8.GetBytes($"{AbsDevAddress:X4}");
            TxBuffer[ReqLen++] = t[2];
            TxBuffer[ReqLen++] = t[3];
            TxBuffer[ReqLen++] = t[0];
            TxBuffer[ReqLen++] = t[1];

            TxBuffer[ReqLen++] = ETX;

            t = MFX_Protocol.ComputeChecksum(TxBuffer, 1, ReqLen - 1);

            TxBuffer[ReqLen++] = t[0];
            TxBuffer[ReqLen++] = t[1];

            //        ACK or NACK
            RespLen = 1;

            if (!_Interface.FlushInputBuffer()) { state = MFX_State.Error_TX; return state; }
            if (_Interface.Write(TxBuffer, 0, ReqLen) != ReqLen) { state = MFX_State.Error_TX; return state; }

            state = AwaitCheckResponse();

            return state;
        }

        /// <summary>
        /// Reads byte data from the CPU
        /// </summary>
        /// <param name="BitType">memory type</param>
        /// <param name="StartingAddress">address of the byte memory to start at</param>
        /// <param name="len">number of bytes to be read frro the CPU</param>
        /// <param name="Data">location where the read data is stored</param>
        /// <returns>status of the transaction</returns>
        public MFX_State ReadNumericData_16B(RegisterType RegType, ushort StartingAddress, ushort len, out ushort[] ReadData)
        {
            lock (lockObj)
            {
                ReadData = new ushort[len];
                MFX_State state = MFX_State.Processing;

                ReqLen = 0;

                state = CheckAddressAndLength(RegType, StartingAddress, len);

                if (state != MFX_State.OK) { return state; }

                if (RegType < RegisterType.Timer_Counter_16B) { state = MFX_State.Error_Incorrect_Data_Type; return state; }

                TxBuffer[ReqLen++] = STX;
                TxBuffer[ReqLen++] = 0x30;

                var t = Encoding.UTF8.GetBytes($"{AbsDevAddress:X4}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];
                TxBuffer[ReqLen++] = t[2];
                TxBuffer[ReqLen++] = t[3];

                t = Encoding.UTF8.GetBytes($"{BytesToRead:X2}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                TxBuffer[ReqLen++] = ETX;

                t = MFX_Protocol.ComputeChecksum(TxBuffer, 1, ReqLen - 1);

                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                //      STX + ETX + CHK
                RespLen = 4 + (BytesToRead * 2);

                if (!_Interface.FlushInputBuffer()) { state = MFX_State.Error_TX; return state; }
                if (_Interface.Write(TxBuffer, 0, ReqLen) != ReqLen) { state = MFX_State.Error_TX; return state; }

                state = AwaitCheckResponse();

                if (state == MFX_State.OK)
                {
                    byte[] Data = ParseData(RxBuffer, 1, BytesToRead);

                    for (int x = 0; x < len; x++)
                    {
                        ReadData[x] = (ushort)(Data[(2 * x)] | ((int)Data[1 + (2 * x)]) << 8);
                    }
                }
                return state;
            }
        }

        /// <summary>
        /// Reads byte data from the CPU in 32-bit formats, useful for 32-bit counters
        /// </summary>
        /// <param name="BitType">memory type</param>
        /// <param name="StartingAddress">address of the byte memory to start at</param>
        /// <param name="len">number of bytes to be read frro the CPU</param>
        /// <param name="Data">location where the read data is stored</param>
        /// <returns>status of the transaction</returns>
        public MFX_State ReadNumericData_32B(RegisterType RegType, ushort StartingAddress, ushort len, out UInt32[] ReadData)
        {
            lock (lockObj)
            {
                ReadData = new UInt32[len];
                MFX_State state = MFX_State.Processing;

                ReqLen = 0;

                state = CheckAddressAndLength(RegType, StartingAddress, len);

                if (state != MFX_State.OK) { return state; }

                if (RegType != RegisterType.Counter_32B) { state = MFX_State.Error_Incorrect_Data_Type; return state; }

                TxBuffer[ReqLen++] = STX;
                TxBuffer[ReqLen++] = 0x30;

                var t = Encoding.UTF8.GetBytes($"{AbsDevAddress:X4}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];
                TxBuffer[ReqLen++] = t[2];
                TxBuffer[ReqLen++] = t[3];

                t = Encoding.UTF8.GetBytes($"{BytesToRead:X2}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                TxBuffer[ReqLen++] = ETX;

                t = MFX_Protocol.ComputeChecksum(TxBuffer, 1, ReqLen - 1);

                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                //      STX + ETX + CHK
                RespLen = 4 + (BytesToRead * 2);

                if (!_Interface.FlushInputBuffer()) { state = MFX_State.Error_TX; return state; }
                if (_Interface.Write(TxBuffer, 0, ReqLen) != ReqLen) { state = MFX_State.Error_TX; return state; }

                state = AwaitCheckResponse();

                if (state == MFX_State.OK)
                {
                    byte[] Data = ParseData(RxBuffer, 1, BytesToRead);

                    for (int x = 0; x < len; x++)
                    {
                        ReadData[x] = (Data[(2 * x)]) | (((uint)Data[1 + (2 * x)]) << 8) | (((uint)Data[2 + (2 * x)]) << 16) | (((uint)Data[3 + (2 * x)]) << 24);
                    }
                }
                return state;
            }
        }

        /// <summary>
        /// Writes numeric data to the CPU in the form of bytes
        /// Warning: when tested with the Chinese FX plc, the write was only successful with the D-types
        /// </summary>
        /// <param name="BitType">memory type</param>
        /// <param name="StartingAddress">Base address of the bit memory to start at</param>
        /// <param name="len">length of data to be written in bytes</param>
        /// <param name="Data">Content to be written</param>
        /// <returns></returns>
        public MFX_State WriteNumericData_16B(RegisterType RegType, ushort StartingAddress, byte len, ushort[] WriteData)
        {
            lock (lockObj)
            {
                MFX_State state = MFX_State.Processing;

                ReqLen = 0;

                state = CheckAddressAndLength(RegType, StartingAddress, len);
                if (state != MFX_State.OK) { return state; }

                if (RegType < RegisterType.Timer_Counter_16B) { state = MFX_State.Error_Incorrect_Data_Type; return state; }


                TxBuffer[ReqLen++] = STX;
                TxBuffer[ReqLen++] = 0x31; //write data

                var t = Encoding.UTF8.GetBytes($"{AbsDevAddress:X4}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];
                TxBuffer[ReqLen++] = t[2];
                TxBuffer[ReqLen++] = t[3];

                t = Encoding.UTF8.GetBytes($"{BytesToRead:X2}");
                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                byte[] EncData = new byte[len * 2]; //1 word takes 2 bytes
                                                    //load data into the TX buffer
                for (int x = 0; x < len; x++)
                {
                    EncData[2 * x] = (byte)(0xff & WriteData[x]);
                    EncData[(2 * x) + 1] = (byte)(WriteData[x] >> 8);
                }
                EncodeData(TxBuffer, EncData, ReqLen, EncData.Length);

                ReqLen += (len * 4); //1 word takes 4 tx bytes

                TxBuffer[ReqLen++] = ETX;

                t = MFX_Protocol.ComputeChecksum(TxBuffer, 1, ReqLen - 1);

                TxBuffer[ReqLen++] = t[0];
                TxBuffer[ReqLen++] = t[1];

                //        ACK or NACK
                RespLen = 1;

                if (!_Interface.FlushInputBuffer()) { state = MFX_State.Error_TX; return state; }
                if (_Interface.Write(TxBuffer, 0, ReqLen) != ReqLen) { state = MFX_State.Error_TX; return state; }

                state = AwaitCheckResponse();

                return state;
            }
        }

        /// <summary>
        /// Parses data from the ASCII-HEX represented format to a series of bytes
        /// </summary>
        /// <param name="InputData">input ASCII UTF8 data</param>
        /// <param name="Offset">starting point for parsing</param>
        /// <param name="NumofBytes">number of result bytes</param>
        /// <returns>the decoded byte array</returns>
        private byte[] ParseData(byte[] InputData, int Offset, int NumofBytes)
        {
            byte[] Res = new byte[NumofBytes];
            for (int x = 0; x < NumofBytes; x++)
            {
                var t = Encoding.UTF8.GetString(InputData, Offset + (2 * x), 2);
                Res[x] = byte.Parse(t, System.Globalization.NumberStyles.HexNumber);
            }
            return Res;
        }

        /// <summary>
        /// Encodes byte data into a HEX-ASCII byte array
        /// </summary>
        /// <param name="ResultArray">where the encoded data should be plugged in</param>
        /// <param name="InputData">input array data to be encoded</param>
        /// <param name="Offset">where in the Result array should the result be placed</param>
        /// <param name="NumofBytes">number of input data bytes to be encoded</param>
        private void EncodeData(byte[] ResultArray, byte[] InputData, int Offset, int NumofBytes)
        {
            for (int x = 0; x < NumofBytes; x++)
            {
                var y = Encoding.UTF8.GetBytes($"{InputData[x]:X2}");
                ResultArray[Offset + (2 * x)] = y[0];
                ResultArray[Offset + (2 * x) + 1] = y[1];
            }
        }

        /// <summary>
        /// Waits for the response to come back and checks it
        /// </summary>
        /// <returns></returns>
        private MFX_State AwaitCheckResponse()
        {
            //attempt to read data
            if (_Interface.Read(RxBuffer, 0, RespLen) >= RespLen)
            {
                if (TxBuffer[1] == 0x30) //for read, perform a checksum check
                {
                    var Checksum = ComputeChecksum(RxBuffer, 1, RespLen - 3);
                    if (Checksum[0] == RxBuffer[RespLen - 2] || Checksum[1] == RxBuffer[RespLen - 1])
                    {
                        return MFX_State.OK;
                    }
                    else
                    {
                        return MFX_State.Error_CRC;
                    }
                }
                else if (TxBuffer[1] == 0x31 || TxBuffer[1] == 0x37 || TxBuffer[1] == 0x38) //for write check for the ACk/NACK
                {
                    return (RxBuffer[0] == ACK) ? MFX_State.OK : MFX_State.Error_Ack;
                }
                else
                {
                    return MFX_State.Error_Ack;
                }
            }
            else
            {
                return MFX_State.Error_Timeout;
            }
        }

        /// <summary>
        /// Computes and returns the 2-byte check sum for a particular data frame
        /// </summary>
        /// <param name="Frame">data frame</param>
        /// <param name="Offset">starting offset at which the checksum calculation will begin</param>
        /// <param name="Length">number of bytes to be included inside the calculation starting at the offset</param>
        /// <returns></returns>
        private static byte[] ComputeChecksum(byte[] Frame, int Offset, int Length)
        {
            byte[] Checksum = new byte[2];
            byte Chk = 0;
            for (int x = 0; x < Length; x++)
            {
                Chk += Frame[Offset + x];
            }
            Checksum = Encoding.UTF8.GetBytes($"{Chk:X2}");
            return Checksum;
        }


        /// <summary>
        /// Checks the address format, range and data length
        /// </summary>
        /// <param name="type"></param>
        /// <param name="startingAddress"></param>
        /// <param name="len">length in the requested units</param>
        /// <returns></returns>
        private MFX_State CheckAddressAndLength(RegisterType type, int Address, int len)
        {
            //do a number base format check
            MFX_State st = Address % 10 > RegMapImages[type].Item4 - 1 ? MFX_State.Error_Incorrect_Address : MFX_State.OK;

            if (st != MFX_State.OK) { return st; }

            //then do an initial address range check
            if (Address < RegMapImages[type].Item2 || Address > RegMapImages[type].Item3)
            {
                st = MFX_State.Error_Address_Out_Of_Range;
                return st;
            }

            //remove the offset if any
            Address -= RegMapImages[type].Item2;

            //get the decimal address
            if (type == RegisterType.Input_Contact || type == RegisterType.Output_Contact)
            {
                DecAddress = 8 * (Address / 10) + Address % 10; //conversion from octal to decimal
            }
            else
            {
                DecAddress = Address;
            }

            //get the starting device address
            switch (type)
            {
                //for boolean types
                case RegisterType.State:
                case RegisterType.Output_Contact:
                case RegisterType.Input_Contact:
                case RegisterType.Timer_Contact:
                case RegisterType.Memory_Contact:
                case RegisterType.M_Special:
                    //calculate the absolute device address to be sent to the CPU
                    AbsDevAddress = RegMapImages[type].Item1 + (DecAddress / 8);
                    //address of the 1st byte to be read
                    FirstByteAddress = DecAddress / 8;
                    //address of the last byte to be read
                    LastByteAddress = (DecAddress + len - 1) / 8;
                    //calculate the num of bytes to be read
                    BytesToRead = (ushort)(LastByteAddress - FirstByteAddress + 1);
                    break;

                //for numeric variables
                case RegisterType.Counter_16B:
                case RegisterType.Timer_Counter_16B:
                case RegisterType.Data:
                case RegisterType.Data_Special:
                    //since every variable occupies 2 bytes
                    AbsDevAddress = RegMapImages[type].Item1 + (2 * DecAddress);
                    FirstByteAddress = DecAddress;
                    LastByteAddress = DecAddress + (2 * len) - 1;
                    BytesToRead = (ushort)(LastByteAddress - FirstByteAddress + 1);
                    break;

                case RegisterType.Counter_32B:
                    AbsDevAddress = RegMapImages[type].Item1 + (4 * DecAddress);
                    FirstByteAddress = DecAddress;
                    LastByteAddress = DecAddress + (4 * len) - 1;
                    BytesToRead = (ushort)(LastByteAddress - FirstByteAddress + 1);
                    break;

                default:
                    break;
            }

            //check the requested number of bytes
            if (BytesToRead > 64)
            {
                st = MFX_State.Error_Too_Many_Bytes;
            }
            else
            {
                st = MFX_State.OK;

            }
            return st;
        }

        /// <summary>
        /// Checks an updates the forced bit address
        /// </summary>
        /// <param name="type">only bit types are allowed</param>
        /// <param name="Address">requested bit address</param>
        /// <returns></returns>
        private MFX_State CheckForcedAddres(RegisterType type, int Address)
        {
            MFX_State s = MFX_State.OK;
            //do a number base format check
            s = Address % 10 > RegMapImages[type].Item4 - 1 ? MFX_State.Error_Incorrect_Address : MFX_State.OK;

            if (s != MFX_State.OK) { return s; }

            //check for valid register type
            if (type > RegisterType.M_Special)
            {
                s = MFX_State.Error_Incorrect_Data_Type; return s;
            }
            //check the address range
            if (Address < RegMapImages[type].Item2 || Address > RegMapImages[type].Item3)
            {
                s = MFX_State.Error_Address_Out_Of_Range; return s;
            }

            //get the decimal address
            if (type == RegisterType.Input_Contact || type == RegisterType.Output_Contact)
            {
                DecAddress = 8 * (Address / 10) + Address % 10; //conversion from octal to decimal
            }
            else
            {
                DecAddress = Address - RegMapImages[type].Item2;
            }

            AbsDevAddress = RegMapImages[type].Item6 + DecAddress;
            return s;
        }

        #region Parsing functions
        public static Int32 ParseInt32(ushort[] Data, int Offset)
        {
            List<byte> bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(Data[Offset])); //gets it in little endian format
            bytes.AddRange(BitConverter.GetBytes(Data[Offset + 1])); //gets it in little endian format
            byte[] ParserBytes = new byte[4];
            ParserBytes[0] = bytes[0];
            ParserBytes[1] = bytes[1];
            ParserBytes[2] = bytes[2];
            ParserBytes[3] = bytes[3];
            return BitConverter.ToInt32(ParserBytes.ToArray(), 0);
        }
        public static UInt32 ParseUInt32(ushort[] Data, int Offset)
        {
            List<byte> bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(Data[Offset])); //gets it in little endian format
            bytes.AddRange(BitConverter.GetBytes(Data[Offset + 1])); //gets it in little endian format
            byte[] ParserBytes = new byte[4];
            ParserBytes[0] = bytes[0];
            ParserBytes[1] = bytes[1];
            ParserBytes[2] = bytes[2];
            ParserBytes[3] = bytes[3];
            return BitConverter.ToUInt32(ParserBytes.ToArray(), 0);
        }
        public static float ParseFloat(ushort[] Data, int Offset)
        {
            List<byte> bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(Data[Offset])); //gets it in little endian format
            bytes.AddRange(BitConverter.GetBytes(Data[Offset + 1])); //gets it in little endian format
            byte[] ParserBytes = new byte[4];
            ParserBytes[0] = bytes[0];
            ParserBytes[1] = bytes[1];
            ParserBytes[2] = bytes[2];
            ParserBytes[3] = bytes[3];
            return BitConverter.ToSingle(ParserBytes.ToArray(), 0);
        }
        public static void EncodeInt32(ref ushort[] Data, int Offset, Int32 Value)
        {
            byte[] bytes = BitConverter.GetBytes(Value);
            Data[Offset] = (ushort)(bytes[0] | (bytes[1] << 8));
            Data[Offset + 1] = (ushort)(bytes[2] | (bytes[3] << 8));
        }
        public static void EncodeUInt32(ref ushort[] Data, int Offset, UInt32 Value)
        {
            byte[] bytes = BitConverter.GetBytes(Value);
            Data[Offset] = (ushort)(bytes[0] | (bytes[1] << 8));
            Data[Offset + 1] = (ushort)(bytes[2] | (bytes[3] << 8));
        }
        public static void EncodeFloat(ref ushort[] Data, int Offset, float Value)
        {
            byte[] bytes = BitConverter.GetBytes(Value);
            Data[Offset] = (ushort)(bytes[0] | (bytes[1] << 8));
            Data[Offset + 1] = (ushort)(bytes[2] | (bytes[3] << 8));
        }
        #endregion

        public enum MFX_State
        {
            OK, //Success
            Error_Ack, //recieved an NACK from CPU
            Error_Timeout, //no responce recieved from CPU
            Error_CRC, // CRC error in the CPU response
            Error_TX, // itranslayer transmission problem
            Error_Incorrect_Address, // incorrect address format, for ex. Y8 (X and Y are supposed to be Octal numbers)
            Error_Too_Many_Bytes, // bytes to read/write are > 64 bytes which is the COU transfer limit, try reducing the data size
            Error_Address_Incorrectly_Aligned, //when writing bits, make sure the base address is in the multiples of 8, for ex(M10 is wrong, M8 should be OK)
            Error_Incorrect_Data_Type, // ex. requesting a D type in a bit read/write
            Error_Address_Out_Of_Range, //requesting output no.900 while the max is 177
            Processing, //intermediate state indicating that the communication is in progress
        }

        /*        private static Dictionary<RegisterType, (int, int)> registersMapBits =
                new Dictionary<RegisterType, (int, int)>
                {
                        { RegisterType.State, (0x0000, 8) },//S
                        { RegisterType.Input, (0x0400, 10) },//X
                        { RegisterType.Output, (0x0500, 10) },//Y
                        { RegisterType.Timer_Contact, (0x0600, 8) },//T
                        { RegisterType.Memory, (0x0800, 8) }//M
                };*/

        // Reg Type     Address Offset ,User Input Min Address , User Input Max Address , Number Base , size in bytes, forced bit address offset
        private static Dictionary<RegisterType, (int, int, int, int, int, int)> RegMapImages =
        new Dictionary<RegisterType, (int, int, int, int, int, int)>
        {
            //Bit Types
            { RegisterType.State, (0x0000,0,999,10,0,0x0000) }, //S
            { RegisterType.Input_Contact, (0x0080,0,177,8,0,0x0400)},//X (Octal Formatting)
            { RegisterType.Output_Contact, (0x00a0, 0, 177, 8, 0,0x0500) },//Y (Octal Formatting)
            { RegisterType.Timer_Contact, (0x00c0, 0, 255, 10, 0,0x0600) }, //T
            { RegisterType.Contact, (0x01c0, 0, 255, 10, 0,0x0e00) }, //C
            { RegisterType.Memory_Contact, (0x0100, 0, 1023, 10, 0,0x0800) }, //M
            { RegisterType.M_Special, (0x01e0,8000,8255,10,0,0x0f00) }, //M special (M8xxx)

            //Numeric types
            { RegisterType.Timer_Counter_16B, (0x0800, 0, 255, 10, 2,0) }, //timer counter 16b
            { RegisterType.Counter_16B, (0x0A00, 0, 199, 10, 2,0) }, // counter 16b
            { RegisterType.Counter_32B, (0x0c00,200,255,10,4,0) }, //counter 32b
            { RegisterType.Data, (0x1000,0,511,10,2,0) }, //D
            { RegisterType.Data_Special, (0x0e00,8000,8255,10,2,0) }, //D
        };

        const byte STX = 0x02;  // Start of text
        const byte ETX = 0x3;  // End of text
        const byte EOT = 0x4;  // End of transmission
        const byte ENQ = 0x5; // Enquiry
        const byte ACK = 0x6;  // Acknowledge
        const byte LF = 0xa;  // Line Feed
        const byte CL = 0xc;  // Clear
        const byte CR = 0xd;  // Carrier Return
        const byte NAK = 0x15; // Not Acknowledge
    }

    public enum RegisterType : byte
    {
        State, //S
        Input_Contact, //X
        Output_Contact,//Y
        Timer_Contact, //T
        Contact, //C
        Memory_Contact, //M
        M_Special,// special markers

        Counter_32B,

        Timer_Counter_16B,

        Data,   //D
        Data_Special,
        Counter_16B,//C
    }
}





