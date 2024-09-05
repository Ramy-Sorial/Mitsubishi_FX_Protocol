using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mitsubishi_FX
{
    public partial class Form1 : Form
    {
        MFX_Protocol CPU;


        public Form1()
        {
            MFX_SerialTP serial = new MFX_SerialTP("COM5", 38400, System.IO.Ports.Parity.Even, System.IO.Ports.StopBits.One, 7, 1000);

            CPU = new MFX_Protocol(serial);
            CPU.Start();

            /*            bool[] Da = new bool[16];
                        var j = CPU.ReadBitData(RegisterType.Output_Contact, 0, 10, out Da);

                        bool[] tx = new bool[] { true, true, false, false, true, false, true, false };
                        j = CPU.WriteBitData(RegisterType.Memory_Contact, 16, 1, tx);


                        j = CPU.WriteNumericData_16B(RegisterType.Data, 0, 4, new ushort[] { 1, 2, 3, 4 });
                        j = CPU.WriteNumericData_16B(RegisterType.Timer_Counter_16B, 0, 1, new ushort[] { 5000, 5001 });
                        j = CPU.WriteNumericData_16B(RegisterType.Counter_16B, 0, 2, new ushort[] { 10020, 10021 });
                        j = CPU.WriteNumericData_16B(RegisterType.Data_Special, 8134, 1, new ushort[] { 61000 });

                        uint[] f;
                        j = CPU.ReadNumericData_32B(RegisterType.Counter_32B, 200, 2, out f);

                        ushort[] data = new ushort[32];
                        j = CPU.ReadNumericData_16B(RegisterType.Data, 0, 4, out data);
                        j = CPU.ReadNumericData_16B(RegisterType.Timer_Counter_16B, 0, 2, out data);
                        j = CPU.ReadNumericData_16B(RegisterType.Counter_16B, 0, 2, out data);
                        j = CPU.ReadNumericData_16B(RegisterType.Data_Special, 8134, 1, out data);*/

            //CPU.Force_Bit(RegisterType.Memory_Contact, 1000, false);

            int Offset = 0;
            
            ushort[]Tdata = new ushort[32];
            MFX_Protocol.EncodeFloat(ref Tdata, Offset,1.25f );Offset += 2;
            MFX_Protocol.EncodeUInt32(ref Tdata, Offset, 150000); Offset += 2;
            MFX_Protocol.EncodeInt32(ref Tdata, Offset, -160000); Offset += 2;
            var t = CPU.WriteNumericData_16B(RegisterType.Data, 0, (byte)Offset, Tdata);

            ushort[] Data = new ushort[32];

            t = CPU.ReadNumericData_16B(RegisterType.Data, 0, 8, out Data);

            Offset = 0;

            float F = MFX_Protocol.ParseFloat(Data, Offset); Offset += 2;
            UInt32 G = MFX_Protocol.ParseUInt32(Data, Offset); Offset += 2;
            Int32 H = MFX_Protocol.ParseInt32(Data, Offset); Offset += 2;

            InitializeComponent();
        }
    }
}
