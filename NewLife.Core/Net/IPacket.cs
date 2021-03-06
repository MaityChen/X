﻿using System;
using System.IO;

namespace NewLife.Net
{
    /// <summary>封包接口</summary>
    public interface IPacket
    {
        /// <summary>分析数据流，得到一帧数据</summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        Stream Parse(Stream stream);
    }

    /// <summary>头部指明长度的封包格式</summary>
    public class HeaderLengthPacket : IPacket
    {
        #region 属性
        /// <summary>长度所在位置，默认0</summary>
        public Int32 Offset { get; set; }

        /// <summary>长度占据字节数，1/2/4个字节，默认0表示压缩编码整数</summary>
        public Int32 Size { get; set; }

        /// <summary>过期时间，超过该时间后按废弃数据处理，默认3000ms</summary>
        public Int32 Expire { get; set; } = 3000;

        private DateTime _last;
        #endregion

        /// <summary>内部缓存</summary>
        private MemoryStream _ms;

        /// <summary>分析数据流，得到一帧数据</summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public Stream Parse(Stream stream)
        {
            var nodata = _ms == null || _ms.Position == _ms.Length;

            // 内部缓存没有数据，直接判断输入数据流是否刚好一帧数据，快速处理，绝大多数是这种场景
            if (nodata)
            {
                if (stream == null) return null;

                var len = GetLength(stream);
                if (len > 0 && stream.Position + len == stream.Length) return stream;
            }

            if (_ms == null) _ms = new MemoryStream();

            // 加锁，避免多线程冲突
            lock (_ms)
            {
                if (stream != null)
                {
                    //Log.XTrace.WriteLine("Parse {0} {1:X2}", stream.Length, stream.ReadByte());
                    //stream.Seek(-1, SeekOrigin.Current);
                    //System.Threading.Thread.Sleep(10);

                    // 超过该时间后按废弃数据处理
                    var now = DateTime.Now;
                    if (_last.AddMilliseconds(Expire) < now)
                    {
                        _ms.SetLength(0);
                        _ms.Position = 0;
                    }
                    _last = now;

                    // 拷贝数据到最后面
                    var p = _ms.Position;
                    _ms.Position = _ms.Length;
                    stream.CopyTo(_ms);
                    _ms.Position = p;
                }
                //Log.XTrace.WriteLine("Parse _ms={0} {1}", _ms.Position, _ms.Length);

                var len = GetLength(_ms);
                if (len <= 0) return null;

                // 长度足够，返回数据帧
                var rs = Sub(_ms, len);
                _ms.Seek(len, SeekOrigin.Current);
                return rs;
            }
        }

        /// <summary>从数据流中获取整帧数据长度</summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        protected virtual Int32 GetLength(Stream stream)
        {
            var p = stream.Position;
            // 数据不够，连长度都读取不了
            if (p + Offset >= stream.Length) return 0;

            // 移动到长度所在位置
            if (Offset > 0) stream.Seek(Offset, SeekOrigin.Current);

            // 读取大小
            var len = 0;
            switch (Size)
            {
                case 0:
                    len = stream.ReadEncodedInt();
                    break;
                case 1:
                    len = stream.ReadByte();
                    break;
                case 2:
                    len = stream.ReadBytes(2).ToInt();
                    break;
                case 4:
                    len = (Int32)stream.ReadBytes(4).ToUInt32();
                    break;
                default:
                    throw new NotSupportedException();
            }

            // 判断后续数据是否足够
            if (stream.Position + len > stream.Length)
            {
                // 长度不足，恢复位置
                stream.Position = p;
                return 0;
            }

            // 数据长度加上头部长度
            len += (Int32)(stream.Position - p);

            // 恢复位置
            stream.Position = p;

            return len;
        }

        /// <summary>创建子数据流</summary>
        /// <param name="stream"></param>
        /// <param name="len"></param>
        /// <returns></returns>
        protected virtual Stream Sub(Stream stream, Int32 len)
        {
            return new StreamSegment(stream, len);
        }

#if DEBUG
        /// <summary>粘包测试</summary>
        public static void Test()
        {
            var svr = new NetServer();
            svr.Port = 777;
            svr.SessionPacket = new HeaderLengthPacket();
            svr.Log = Log.XTrace.Log;
            svr.LogReceive = true;
            svr.Start();

            // 凑齐10个带有长度的数据帧一起发出
            var ms = new MemoryStream();
            for (int i = 0; i < 5; i++)
            {
                var size = i < 4 ? Security.Rand.Next(1400) : Security.Rand.Next(2000, 30000);
                var str = Security.Rand.NextString(size);
                var s = str.Substring(0, Math.Min(str.Length, 16));
                //var h = str.GetBytes().ToHex();
                var mm = new MemoryStream();
                mm.WriteArray(str.GetBytes());
                var h = mm.ToArray().ToHex();
                h = h.Substring(0, Math.Min(h.Length, 32));
                Console.WriteLine("{0}\t{1}\t{2}", mm.ToArray().Length, s, h);

                ms.WriteArray(str.GetBytes());
            }

            var client = new NetUri("tcp://127.0.0.1:777").CreateRemote();
            //client.Remote.Address = NetHelper.MyIP();
            //client.Remote.Address = System.Net.IPAddress.Parse("1.0.0.13");
            client.Log = Log.XTrace.Log;
            client.LogSend = true;
            //client.BufferSize = 1500;
            client.SendAsync(ms.ToArray());

            Console.ReadKey(true);

            client.Close();
            svr.Dispose();
        }
#endif
    }

    /// <summary>数据流包装，表示一个数据流的子数据流</summary>
    class StreamSegment : Stream
    {
        #region 属性
        /// <summary>主数据流</summary>
        Stream _s;
        /// <summary>子数据流在主数据流中的偏移</summary>
        Int64 _offset;

        public override Boolean CanRead => _s.CanRead;

        public override Boolean CanSeek => _s.CanSeek;

        public override Boolean CanWrite => _s.CanWrite;

        public override Int64 Length { get; }

        public override Int64 Position { get; set; }
        //public override Int64 Position
        //{
        //    get
        //    {
        //        return _s.Position - _offset;
        //    }

        //    set
        //    {
        //        if (value < 0 || value >= Length) throw new ArgumentOutOfRangeException(nameof(value));

        //        _s.Position = value + _offset;
        //    }
        //}
        #endregion

        #region 构造
        public StreamSegment(Stream stream, Int32 len)
        {
            _s = stream;
            _offset = stream.Position;
            Length = len;
        }
        #endregion

        #region 常用方法
        public override void Flush()
        {
            _s.Flush();
        }

        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
        {
            // 读写前后，控制好数据流指针
            var p = _s.Position;
            _s.Position = Position + _offset;
            try
            {
                var rs = _s.Read(buffer, offset, count);
                Position += (_s.Position - p);
                return rs;
            }
            finally
            {
                _s.Position = p;
            }
        }

        public override void Write(Byte[] buffer, Int32 offset, Int32 count)
        {
            // 读写前后，控制好数据流指针
            var p = _s.Position;
            _s.Position = Position + _offset;
            try
            {
                _s.Write(buffer, offset, count);
                Position += (_s.Position - p);
            }
            finally
            {
                _s.Position = p;
            }
        }

        public override Int64 Seek(Int64 offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Current:
                    return _s.Seek(offset, origin);
                case SeekOrigin.Begin:
                case SeekOrigin.End:
                default:
                    return _s.Seek(_offset + offset, origin);
            }
        }

        public override void SetLength(Int64 value)
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}