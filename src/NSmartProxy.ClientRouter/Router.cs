﻿using NSmartProxy.Data;
using NSmartProxy.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSmartProxy.Shared;

namespace NSmartProxy.Client
{
    public class NullLogger : INSmartLogger
    {
        public void Debug(object message)
        {
            //Not Implemented
        }

        public void Error(object message, Exception ex)
        {
            //Not Implemented
        }

        public void Info(object message)
        {
            //Not Implemented
        }
    }

    public enum ClientStatus
    {
        Stopped = 0,
        Started = 1
    }

    public class Router
    {
        CancellationTokenSource ONE_LIVE_TOKEN_SRC;
        CancellationTokenSource CANCEL_TOKEN_SRC;
        CancellationTokenSource TRANSFERING_TOKEN_SRC;
        CancellationTokenSource HEARTBEAT_TOKEN_SRC;
        TaskCompletionSource<object> _waiter;

        public ServerConnnectionManager ConnectionManager;
        public bool IsStarted = false;

        internal Config ClientConfig;
        internal static INSmartLogger Logger = new NullLogger();   //inject


        public Action DoServerNoResponse = delegate { };
        public Action<ClientStatus> StatusChanged = delegate { };

        public Router()
        {
            ONE_LIVE_TOKEN_SRC = new CancellationTokenSource();
        }

        public Router(INSmartLogger logger) : this()
        {
            Logger = logger;
        }

        public void SetConifiguration(Config config)
        {
            ClientConfig = config;
        }

        /// <summary>
        /// 重要：连接服务端，一般做为入口方法
        /// 该方法主要操作一些配置和心跳
        /// </summary>
        /// <returns></returns>
        public async Task Start()
        {
            var oneLiveToken = ONE_LIVE_TOKEN_SRC.Token;
            while (!oneLiveToken.IsCancellationRequested)
            {
                CANCEL_TOKEN_SRC = new CancellationTokenSource();
                TRANSFERING_TOKEN_SRC = new CancellationTokenSource();
                HEARTBEAT_TOKEN_SRC = new CancellationTokenSource();
                _waiter = new TaskCompletionSource<object>();
                var appIdIpPortConfig = ClientConfig.Clients;

                //1.获取配置
                ConnectionManager = ServerConnnectionManager.Create();
                ConnectionManager.ClientGroupConnected += ServerConnnectionManager_ClientGroupConnected;
                ConnectionManager.ServerNoResponse = DoServerNoResponse;//下钻事件
                ClientModel clientModel = null;//
                try
                {
                    clientModel = await ConnectionManager.InitConfig(this.ClientConfig).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    //TODO 状态码：连接失败
                    Router.Logger.Error("连接失败：" + ex.Message, ex);
                    //throw;
                }
                if (clientModel != null)
                {
                    int counter = 0;
                    //2.分配配置：appid为0时说明没有分配appid，所以需要分配一个
                    foreach (var app in appIdIpPortConfig)
                    {
                        if (app.AppId == 0)
                        {
                            app.AppId = clientModel.AppList[counter].AppId;
                            counter++;
                        }
                    }
                    Logger.Debug("****************port list*************");

                    foreach (var ap in clientModel.AppList)
                    {
                        var cApp = appIdIpPortConfig.First(obj => obj.AppId == ap.AppId);
                        Logger.Debug(ap.AppId.ToString() + ":  " + ClientConfig.ProviderAddress + ":" + ap.Port.ToString() + "=>" +
                             cApp.IP + ":" + cApp.TargetServicePort);
                    }
                    Logger.Debug("**************************************");
                    ConnectionManager.PollingToProvider(StatusChanged);
                    //3.创建心跳连接
                    ConnectionManager.StartHeartBeats(Global.HeartbeatInterval, HEARTBEAT_TOKEN_SRC.Token);

                    //try
                    //{
                    //    await pollingTask.ConfigureAwait(false);
                    //}
                    //catch (Exception ex)
                    //{
                    //    Logger.Error("Thread:" + Thread.CurrentThread.ManagedThreadId + " crashed.\n", ex);
                    //    throw;
                    //}
                    IsStarted = true;
                    Exception exception = await _waiter.Task.ConfigureAwait(false) as Exception;
                    Router.Logger.Debug($"程序异常终止:{exception.Message}。");
                }
                else
                {
                    Router.Logger.Debug($"程序启动失败。");
                    //如果程序从未启动过就出错，则终止程序，否则重试。
                    if (IsStarted == false) { StatusChanged(ClientStatus.Stopped); return; }

                }
                //出错重试
                await Task.Delay(3000, ONE_LIVE_TOKEN_SRC.Token);
                //TODO 返回错误码
                //await Task.Delay(TimeSpan.FromHours(24), CANCEL_TOKEN.Token).ConfigureAwait(false);
            }
            //正常终止
        }

        private void ServerConnnectionManager_ClientGroupConnected(object sender, EventArgs e)
        {
            var args = (ClientGroupEventArgs)e;
            foreach (TcpClient providerClient in args.NewClients)
            {
                Router.Logger.Debug("Open server connection.");
                OpenTrasferation(args.App.AppId, providerClient);
            }

        }

        public async Task Close()
        {
            try
            {
                var config = ClientConfig;
                //客户端关闭
                CANCEL_TOKEN_SRC.Cancel();
                TRANSFERING_TOKEN_SRC.Cancel();
                HEARTBEAT_TOKEN_SRC.Cancel();
                ONE_LIVE_TOKEN_SRC.Cancel();
                _waiter.SetCanceled();
                //服务端关闭
                await NetworkUtil.ConnectAndSend(
                        config.ProviderAddress,
                    config.ProviderConfigPort,
                        Protocol.CloseClient,
                        StringUtil.IntTo2Bytes(this.ConnectionManager.ClientID),
                        true)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Router.Logger.Debug("关闭失败！" + ex);
            }
        }

        private async Task OpenTrasferation(int appId, TcpClient providerClient)
        {
            TcpClient toTargetServer = new TcpClient();
            //事件循环2
            try
            {
                byte[] buffer = new byte[1];
                NetworkStream providerClientStream = providerClient.GetStream();
                //接收首条消息，首条消息中返回的是appid和客户端
                //TODO 客户端长连接，需要保活，终止则说明服务端断开
                // providerClient.keep
                // providerClient.Client.

                //try
                //{
                int readByteCount = await providerClientStream.ReadAsync(buffer, 0, buffer.Length);
                if (readByteCount == 0)
                {
                    Router.Logger.Debug("服务器状态异常，已断开连接");
                    return;
                }
                //}
                //catch
                //{
                //    //此线程出错后，应用程序需要重置，并重启
                //}

                //连接后从TcpClientGroup空闲连接列表中移除该client
                if (ConnectionManager.ExistClient(appId, providerClient))
                {
                    var removedClient = ConnectionManager.RemoveClient(appId, providerClient);
                    if (removedClient == null)
                    {

                        return;
                    }
                }
                else
                {
                    Router.Logger.Debug($"已无法在{appId}中找到客户端 hash:{providerClient.GetHashCode()}.");
                    return;
                }
                //每移除一个链接则发起一个新的链接
                Router.Logger.Debug(appId + "接收到连接请求");
                //根据clientid_appid发送到固定的端口
                //TODO 序列没有匹配元素？
                ClientApp item = ClientConfig.Clients.First((obj) => obj.AppId == appId);

                //向服务端发起一次长连接，没有接收任何外来连接请求时，
                //该方法会在write处会阻塞。
                await ConnectionManager.ConnectAppToServer(appId);
                Router.Logger.Debug("已建立反向连接:" + appId);
                // item1:app编号，item2:ip地址，item3:目标服务端口
                toTargetServer.Connect(item.IP, item.TargetServicePort);
                Router.Logger.Debug("已连接目标服务:" + item.IP.ToString() + ":" + item.TargetServicePort.ToString());

                NetworkStream targetServerStream = toTargetServer.GetStream();
                //targetServerStream.Write(buffer, 0, readByteCount);
                TcpTransferAsync(providerClientStream, targetServerStream, providerClient, toTargetServer);
                //already close connection

            }
            catch (Exception ex)
            {
                Logger.Debug("传输时出错：" + ex);
                //关闭传输连接，服务端也会相应处理，把0request发送给消费端
                //TODO ***: 连接时出错，重启客户端
                _waiter.TrySetResult(ex);
                toTargetServer.Close();
                providerClient.Close();
                throw;
            }

        }


        private async Task TcpTransferAsync(NetworkStream providerStream, NetworkStream targetServceStream, TcpClient providerClient, TcpClient toTargetServer)
        {
            try
            {
                Router.Logger.Debug("Looping start.");
                //创建相互转发流
                var taskT2PLooping = ToStaticTransfer(TRANSFERING_TOKEN_SRC.Token, targetServceStream, providerStream, "T2P");
                var taskP2TLooping = StreamTransfer(TRANSFERING_TOKEN_SRC.Token, providerStream, targetServceStream, "P2T");

                //close connnection,whether client or server stopped transferring.
                var comletedTask = await Task.WhenAny(taskT2PLooping, taskP2TLooping);
                //Router.Logger.Debug(comletedTask.Result + "传输关闭，重新读取字节");
                providerClient.Close();
                Router.Logger.Debug("已关闭toProvider连接。");
                toTargetServer.Close();
                Router.Logger.Debug("已关闭toTargetServer连接。");
            }
            catch (Exception ex)
            {
                Router.Logger.Debug(ex.ToString());
                throw;
            }
        }



        private async Task<string> StreamTransfer(CancellationToken ct, NetworkStream fromStream, NetworkStream toStream, string signal, Func<byte[], Task<bool>> beforeTransfer = null)
        {
            try
            {
                await fromStream.CopyToAsync(toStream, 4096, ct);
            }
            catch (Exception ex)
            {
                Router.Logger.Debug(ex.ToString());
                throw;
            }



            return signal;
        }


        private async Task<string> ToStaticTransfer(CancellationToken ct, NetworkStream fromStream, NetworkStream toStream, string signal, Func<byte[], Task<bool>> beforeTransfer = null)
        {
            try
            {
                await fromStream.CopyToAsync(toStream, 4096, ct);
            }
            catch (Exception ex)
            {
                Router.Logger.Debug(ex.ToString());
                throw;
            }
            return signal;
        }

        private void SendZero(int port)
        {
            TcpClient tc = new TcpClient();
            tc.Connect("127.0.0.1", port);
            tc.Client.Send(new byte[] { 0x00 });
        }
    }

}
