using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DT4000Monitor
{
    public class Monitor
    {
        /// <summary>
        /// 服务器IP
        /// </summary>
        public string ServerIP { get; set; }
        /// <summary>
        /// 服务器端口
        /// </summary>
        public int ServerPort { get; set; }
        /// <summary>
        /// DT4000 IP
        /// </summary>
        public string ClientIP { get; set; }
        /// <summary>
        /// DT4000 端口
        /// </summary>
        public int ClientPort { get; set; }
        /// <summary>
        /// DT4000 状态
        /// </summary>
        public DT4000Status Status { get; set; }
        /// <summary>
        /// 失败时蜂鸣器响声次数
        /// </summary>
        public int BeeperCount { get; set; }

        /// <summary>
        /// 是否开启侦听
        /// </summary>
        private bool _bstop;
        /// <summary>
        /// 是否过滤侦听
        /// </summary>
        private bool _filtrateLog = true;
        private Thread _threadsock;
        private Socket _listener;
        private static ManualResetEvent AllDone = new ManualResetEvent(false);
        struct SocketList
        {
            public IPAddress Ipaddr;
            public Socket Handler;
        }
        private readonly ArrayList _socketAl = new ArrayList();
        private bool _firstRead;
        private int _countFailDT4000;

        #region 侦听
        /// <summary>
        /// 开始侦听
        /// </summary>
        public void Listen()
        {
            try
            {
                _bstop = false;
                _threadsock = new Thread(StartListening);
                _threadsock.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] Listen::" + ex.Message);
            }
        }

        private void StartListening()
        {
            try
            {
                var serverIP = IPAddress.Parse(ServerIP);
                var clientIP = IPAddress.Parse(ClientIP);
                Console.WriteLine("StartListening::ServerIP = " + serverIP + ", ClientIP = " + clientIP);
                // Establish the local endpoint for the socket.  建立本地端点的socket,
                var localEndPoint = new IPEndPoint(serverIP, ServerPort);
                // Create a TCP/IP socket.
                _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                // Bind the socket to the local endpoint and listen for incoming connections.
                _listener.Bind(localEndPoint);
                _listener.Listen(100);
                if (!_bstop)
                {
                    // Set the event to nonsignaled state. 设置事件为无信号状态
                    AllDone.Reset();
                    // Start an asynchronous socket to listen for connections. 开始异步连接
                    Console.WriteLine("StartListening::等待连接中...");
                    _listener.BeginAccept(new AsyncCallback(AcceptCallback), _listener);
                    // Wait until a connection is made before continuing. 开始前，等待一个连接
                    AllDone.WaitOne();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] StartListening::" + ex.Message);
            }
        }

        private void AcceptCallback(IAsyncResult ar)
        {
            try
            {
                // Signal the main thread to continue. 信号主线程继续
                if (_bstop)
                    return;
                AllDone.Set();
                // Get the socket that handles the client request. 获取socket,处理客户端请求
                var listener = (Socket)ar.AsyncState;
                var handler = listener.EndAccept(ar);
                // Create the state object. 创建对象状态
                var state = new StateObject
                {
                    WorkSocket = handler
                };
                handler.BeginReceive(state.Buffer, 0, StateObject.BufferSize, 0, new AsyncCallback(ReadCallback), state);
                UpdateSocketList(state);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] AcceptCallback::" + ex.Message);
            }
        }

        private void ReadCallback(IAsyncResult ar)
        {
            // Retrieve the state object and the handler socket from the asynchronous state object.
            var state = (StateObject)ar.AsyncState;
            var handler = state.WorkSocket;
            // Read data from the client socket. 
            try
            {
                if (!_bstop)
                {
                    var bytesRead = handler.EndReceive(ar);
                    if (bytesRead > 0)
                    {
                        ConvertMsg(state.WorkSocket.RemoteEndPoint.ToString(), state.Buffer, bytesRead);
                        state.Sb.Remove(0, state.Sb.Length);
                    }
                    // Not all data received. Get more.                
                    if (bytesRead == 0) //断开连接
                    {
                        state.WorkSocket.Close();
                        UpdateSocketList(state);
                        //状态                        
                        if (Status != DT4000Status.Stop)
                        {
                            Status = DT4000Status.Stop;   //_DT4000未监听
                            Console.WriteLine("未监听...");
                        }
                        Console.WriteLine("ReadCallback::与DT4000的通讯已断开！");
                    }
                    if (state.WorkSocket.Connected)
                    {
                        if (!_firstRead)
                        {
                            SendMessage("ConnectedSuccess..." + "\r" + "EnterBoxNo::");
                            Console.WriteLine("ReadCallback::" + "EquipmentAt" + DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + "ConnectedSuccess");// 首次连接成功！设备于 ：
                            _firstRead = true;
                        }
                        //状态                        
                        if (Status != DT4000Status.Run)
                        {
                            Status = DT4000Status.Run;
                            Console.WriteLine("监听中...");
                        }
                        handler.BeginReceive(state.Buffer, 0, StateObject.BufferSize, 0, new AsyncCallback(ReadCallback), state);
                    }
                    else
                    {
                        UpdateSocketList(state);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] ReadCallback::" + ex.Message);
                return;
            }
        }
        #endregion

        #region 处理接收到的消息
        private int _curScreenIndex = 1;

        private readonly Dictionary<string, string> _dictRowScree = new Dictionary<string, string>();

        /// <summary>
        /// 转换接收到的消息
        /// </summary>
        /// <param name="ipaddr"></param>
        /// <param name="buffer"></param>
        /// <param name="bytesRead"></param>
        public void ConvertMsg(string ipaddr, byte[] buffer, int bytesRead)
        {
            try
            {
                #region
                // There  might be more data, so store the data received so far.
                var content = (Encoding.ASCII.GetString(buffer, 0, bytesRead));
                // Check for end-of-file tag. If it is not there, read  more data.
                // All the data has been read from the client. Display it on the console.
                string msg;
                if (content.Length > 8)
                {
                    var tmpByte = new Byte[content.Length - 8];
                    var subcmd = buffer[6];
                    for (var i = 0; i < content.Length - 8; i++)
                    {
                        tmpByte[i] = buffer[i + 8];
                    }
                    switch (subcmd)
                    {
                        case 16://接受DT4000的功能键
                            msg = tmpByte[0].ToString();
                            break;
                        case 55:
                            //接收条码键盘数据从第9位开始
                            msg = Encoding.Default.GetString(tmpByte);
                            break;
                        case 0:
                            msg = Encoding.Default.GetString(tmpByte);
                            break;
                        default:
                            msg = content;
                            break;
                    }
                }
                else
                {
                    msg = content;
                }
                #endregion
                //做显示
                if (msg.Trim().IndexOf('@') > 0)
                {
                    if (!_filtrateLog)
                    {
                        Console.WriteLine("ConvertMsg::ConnectSignal...");
                    }
                }
                else
                {
                    if (msg.IndexOf("\r") >= 0)
                    {
                        string box = "demoBox";//TODO::oqc过站逻辑，此处暂时固定

                        string msgResult;
                        if (box == null)
                        {
                            CmdSetBeeperTime();

                            msgResult = "盒号不存在::" + msg.Trim();
                            SendMessage(msgResult + "\r" + "r请输入盒号::");
                        }
                        else
                        {
                            bool result = true; //TODO::oqc过站逻辑，此处暂时固定

                            if (result)
                            {
                                CmdSetBeeperTimeSuccess();
                                msgResult = "OQC过帐成功::" + msg.Trim();
                            }
                            else
                            {
                                CmdSetBeeperTime();
                                msgResult = "盒号不符合过帐要求::" + msg.Trim();
                            }
                        }

                        SendMessage(msgResult + "\r" + "请输入盒号::");    //发信息到设备
                        Console.WriteLine("ConvertMsg::" + "输入盒号为::" + msg.Trim());   //记录处理结果
                        Console.WriteLine("ConvertMsg::" + msgResult);   //记录处理结果
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] ConvertMsg::" + ex.Message);
            }

        }

        /// <summary>
        /// StringBusBar过站失败响铃次数
        /// </summary>
        private void CmdSetBeeperTime()
        {
            try
            {
                const int callTime = 1;
                const int silenceTime = 1;
                var count = BeeperCount;
                var callTimeStr = callTime.ToString("x");
                var silenceTimeStr = Convert.ToString(silenceTime, 16);
                var countStr = Convert.ToString(count, 16);
                callTimeStr = callTimeStr.PadLeft(2, '0');
                silenceTimeStr = silenceTimeStr.PadLeft(2, '0');
                countStr = countStr.PadLeft(2, '0');
                var beeper = @"0b\00\40\00\00\00\0b\00\" + callTimeStr + @"\" + silenceTimeStr + @"\" + countStr;
                SendDefault(beeper);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] CmdSetBeeperTime::" + ex.Message);
            }
        }

        /// <summary>
        /// 过站成功响铃次数
        /// </summary>
        private void CmdSetBeeperTimeSuccess()
        {
            try
            {
                const int callTime = 1;
                const int silenceTime = 1;
                const int count = 1;
                var callTimeStr = callTime.ToString("x");
                var silenceTimeStr = Convert.ToString(silenceTime, 16);
                var countStr = Convert.ToString(count, 16);
                callTimeStr = callTimeStr.PadLeft(2, '0');
                silenceTimeStr = silenceTimeStr.PadLeft(2, '0');
                countStr = countStr.PadLeft(2, '0');
                var beeper = @"0b\00\40\00\00\00\0b\00\" + callTimeStr + @"\" + silenceTimeStr + @"\" + countStr;
                SendDefault(beeper);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] CmdSetBeeperTimeSuccess::" + ex.Message);
            }
        }
        #endregion

        #region Socket状态更新
        /// <summary>
        /// 更新Socket连接列表
        /// </summary>
        /// <param name="state"></param>
        public void UpdateSocketList(StateObject state)
        {
            var find = false;
            try
            {
                var ip = (IPEndPoint)state.WorkSocket.RemoteEndPoint;
                var sl = new SocketList();
                for (var i = 0; i < _socketAl.Count; i++)
                {
                    if (!((SocketList)_socketAl[i]).Ipaddr.Equals(ip.Address))
                        continue;
                    find = true;
                    sl = (SocketList)_socketAl[i];
                    sl.Handler = state.WorkSocket;
                    _socketAl[i] = sl;
                    break;
                }
                if (!find)
                {
                    sl.Handler = state.WorkSocket;
                    sl.Ipaddr = ip.Address;
                    _socketAl.Add(sl);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] UpdateSocketList::" + ex.Message);
            }
        }
        #endregion

        #region 显示信息
        /// <summary>
        /// 发送信息给DT4000
        /// </summary>
        /// <param name="message"></param>
        private void SendMessage(string message)
        {
            try
            {
                CmdClearKeyboard();
                if (message.Trim() == "")
                {
                    Console.WriteLine("空的输入...");//"空的输入"
                }
                //汉字长度不能正确识别，所以转数组取
                var sendLen = 8 + Encoding.Default.GetBytes(message).Length;
                var keyboardText = StrToDefault(message.Trim());
                keyboardText = Convert.ToString(sendLen, 16) + @"\00\40\00\00\00\62\00" + keyboardText;
                SendDefault(keyboardText);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] SendMessage::" + ex.Message);
            }
        }

        /// <summary>
        /// 清屏
        /// </summary>
        private void CmdClearKeyboard()
        {
            const string clearKeyboard = @"08\00\40\00\00\00\30\00";
            SendDefault(clearKeyboard);
        }

        /// <summary>
        /// 发送数据
        /// </summary>
        /// <param name="sendMessage"></param>
        private void SendDefault(string sendMessage)
        {
            try
            {
                var strVal = sendMessage.Split('\\');
                var sendByte = new byte[strVal.Length];
                for (var i = 0; i < strVal.Length; i++)
                {
                    sendByte[i] = (byte)Convert.ToInt16(strVal[i], 16);
                }
                SendData(sendByte);
            }
            catch (Exception)
            {
                Console.WriteLine("连接尚未建立，无法发送！");//连接尚未建立，无法发送！
            }
        }

        /// <summary>
        /// 向选中的listview的IP地址发送bytes数据
        /// </summary>
        /// <param name="data"></param>
        private void SendData(byte[] data)
        {
            try
            {
                if (_socketAl == null || _socketAl.Count <= 0)
                    return;
                for (var i = 0; i < _socketAl.Count; i++)
                {
                    if (!((SocketList)_socketAl[i]).Ipaddr.ToString().Equals(ClientIP))
                        continue;
                    if (!((SocketList)_socketAl[i]).Handler.Connected)
                        continue;
                    ((SocketList)_socketAl[i]).Handler.Send(data);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] SendData::" + ex.Message);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="strIn"></param>
        /// <returns></returns>
        private static string StrToDefault(string strIn)
        {
            var strOut = "";
            try
            {
                var tobyte = System.Text.Encoding.Default.GetBytes(strIn);
                for (var i = 0; i < tobyte.Length; i++)
                {
                    strOut = strOut + @"\" + Convert.ToString(tobyte[i], 16);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] StrToDefault::" + ex.Message);
            }
            return strOut;
        }
        #endregion

        #region 定时监听DT4000运行状况
        /// <summary>
        /// UpdateMonitorTickDate发送时间来监听
        /// </summary>
        public void TimeMonitor(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (!PingIP())
                    return;
                if (!IsActived())
                {
                    if (Status != DT4000Status.Stop)
                    {
                        Status = DT4000Status.Stop;
                        Console.WriteLine("未监听...");// "未监听";
                    }
                    _countFailDT4000++;
                }
                else
                {
                    if (Status != DT4000Status.Run)
                    {
                        Status = DT4000Status.Run;
                        Console.WriteLine("已监听...");// "已监听";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] TimeMonitor::" + ex.Message);
            }
        }

        private bool PingIP()
        {
            try
            {
                var pingSender = new Ping();
                var reply = pingSender.Send(ClientIP);
                if (reply == null || reply.Status != IPStatus.Success)
                {
                    var message = string.Format("地址:{0}连接测试失败！", ClientIP);
                    Console.WriteLine("PingIP" + message);
                    if (Status != DT4000Status.Stop)
                    {
                        Status = DT4000Status.Stop;
                        Console.WriteLine("未监听...");// "未监听";
                    }
                    if (!_filtrateLog)
                    {
                        Console.WriteLine("PingIP::" + message);
                    }
                    return false;
                }
                else
                {
                    var message = string.Format("地址:{0}连接测试成功！", ClientIP);
                    if (Status != DT4000Status.Run)
                    {
                        Status = DT4000Status.Run;
                        Console.WriteLine("监听中...");// "监听中";
                    }
                    if (!_filtrateLog)
                    {
                        Console.WriteLine("PingIP::" + message);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] PingIP::" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 监听DT4000
        /// </summary>
        /// <returns></returns>
        private bool IsActived()
        {
            try
            {
                //is null
                if (_socketAl == null || _socketAl.Count <= 0)
                {
                    Console.WriteLine("IsActived::" + "与DT4000连接中断....");

                    if (Status != DT4000Status.Stop)
                    {
                        Status = DT4000Status.Stop;
                        Console.WriteLine("未监听...");// "未监听";
                    }
                    return false;
                }

                //更新DT4000statusDate
                if (_countFailDT4000 > 2)
                {
                    if (!_filtrateLog)
                    {
                        Console.WriteLine("IsActived::" + "监听到Monitorn程序已恢复....");
                    }
                    _countFailDT4000 = 0;
                }
                if (Status != DT4000Status.Run)
                {
                    Status = DT4000Status.Run;
                    Console.WriteLine("监听中...");// "监听中";
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] IsActived::" + ex.Message);
                return false;
            }
        }
        #endregion

        #region 停止侦听
        /// <summary>
        /// 停止侦听
        /// </summary>
        public void StopListen()
        {
            try
            {
                _bstop = true;
                for (var i = 0; i < _socketAl.Count; i++)
                {
                    if (!((SocketList)_socketAl[i]).Handler.Connected)
                        continue;
                    ((SocketList)_socketAl[i]).Handler.Disconnect(false);
                    ((SocketList)_socketAl[i]).Handler.Close();
                }
                if (_threadsock != null)
                {
                    _threadsock.Abort();
                }
                if (_listener != null)
                {
                    _listener.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION_ERROR] StopListen::" + ex.Message);
            }
        }
        #endregion
    }
}
