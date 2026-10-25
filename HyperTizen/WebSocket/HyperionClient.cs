using Newtonsoft.Json;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.WebSocket.DataTypes;

namespace HyperTizen.WebSocket
{
    internal class HyperionClient
    {
        private const int SendTimeoutMs = 2000;
        private const int ReconnectDelayMs = 3000;
        private const int IdleDelayMs = 500;
        private const int StopWaitMs = 2000;

        private WebSocketClient client;
        private string _uri;
        private int _lastConnectAttempt;

        // Es darf immer nur EINE Capture-Schleife laufen: Die TV-Messfunktionen
        // (Slots in libvideoenhance) sind global. Mehrere Schleifen stoeren sich
        // gegenseitig und machen alles langsamer und unruhiger.
        private readonly object _loopLock = new object();
        private bool _loopRunning;
        private Task _loopTask = Task.CompletedTask;

        public HyperionClient()
        {
            if (!Preference.Contains("rpcServer")) return;

            _uri = Preference.Get<string>("rpcServer");
            BeginConnect(_uri);
            Task.Run(() => Start());
        }

        public void UpdateURI(string uri)
        {
            WebSocketClient old = client;
            if (old != null && old.client != null && old.client.State == WebSocketState.Open)
            {
                try
                {
                    old.client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
                catch
                {
                    // Alte Verbindung ist egal, wir bauen eine neue auf.
                }
            }

            _uri = uri;
            BeginConnect(uri);
        }

        private void BeginConnect(string uri)
        {
            WebSocketClient newClient = new WebSocketClient(uri);
            client = newClient;
            _lastConnectAttempt = Environment.TickCount;

            Task.Run(async () =>
            {
                try
                {
                    await newClient.ConnectAsync();
                }
                catch
                {
                    // Fehlgeschlagen: Die Schleife versucht es nach ReconnectDelayMs erneut.
                }
            });
        }

        public Task Start(bool shouldStart = false)
        {
            if (shouldStart)
            {
                App.Configuration.Enabled = true;
            }

            lock (_loopLock)
            {
                if (_loopRunning)
                {
                    return Task.CompletedTask;
                }

                _loopRunning = true;
                _loopTask = Task.Run(() => RunLoopAsync());
                return _loopTask;
            }
        }

        private async Task RunLoopAsync()
        {
            Task pendingSend = Task.CompletedTask;
            bool released = false;

            try
            {
                if (!Capturer.GetCondition())
                {
                    return;
                }

                while (true)
                {
                    // Pruefen und Freigeben passieren atomar, damit ein gleichzeitiges
                    // Start() nicht verloren geht.
                    lock (_loopLock)
                    {
                        if (!App.Configuration.Enabled)
                        {
                            _loopRunning = false;
                            released = true;
                            break;
                        }
                    }

                    try
                    {
                        ClientWebSocket ws = client != null ? client.client : null;
                        WebSocketState state = ws != null ? ws.State : WebSocketState.None;

                        if (state != WebSocketState.Open)
                        {
                            // Nicht verbunden: nichts erfassen, nicht im Kreis drehen.
                            if (ws != null
                                && state != WebSocketState.None
                                && state != WebSocketState.Connecting
                                && !string.IsNullOrEmpty(_uri)
                                && Environment.TickCount - _lastConnectAttempt > ReconnectDelayMs)
                            {
                                BeginConnect(_uri);
                            }

                            await Task.Delay(IdleDelayMs);
                            continue;
                        }

                        // Nach jedem Messblock sofort senden (statt erst nach allen Punkten).
                        // Das vorherige Bild wird noch gesendet, waehrend der TV schon
                        // den naechsten Block misst.
                        Color[] colors = Capturer.CaptureNextBatch();
                        string image = Capturer.ToImage(colors);

                        Task previous = pendingSend;
                        pendingSend = Task.CompletedTask;
                        await previous;

                        pendingSend = SendImageAsync(ws, image);
                    }
                    catch (Exception)
                    {
                        // Verbindungsfehler o. ae.: kurz warten, dann weiter (ggf. Reconnect).
                        await Task.Delay(IdleDelayMs);
                    }
                }

                try
                {
                    await pendingSend;
                }
                catch
                {
                }
            }
            finally
            {
                if (!released)
                {
                    lock (_loopLock)
                    {
                        _loopRunning = false;
                    }
                }
            }
        }

        public async Task Stop()
        {
            App.Configuration.Enabled = false;

            // Erst die laufende Schleife beenden lassen, damit nicht zwei Stellen
            // gleichzeitig die TV-Messfunktionen benutzen.
            Task running = _loopTask;
            await Task.WhenAny(running, Task.Delay(StopWaitMs));

            Color[] colors = Capturer.GetColors();
            string image = Capturer.ToImage(colors);

            ClientWebSocket ws = client != null ? client.client : null;
            if (ws != null && ws.State == WebSocketState.Open)
            {
                try
                {
                    await SendImageAsync(ws, image);
                }
                catch
                {
                }
            }
        }

        private static async Task SendImageAsync(ClientWebSocket ws, string image)
        {
            // Serialisierung laeuft synchron vor dem ersten await.
            ImageCommand imgCmd = new ImageCommand(image);
            string message = JsonConvert.SerializeObject(imgCmd);
            byte[] buffer = Encoding.UTF8.GetBytes(message);

            using (CancellationTokenSource cts = new CancellationTokenSource(SendTimeoutMs))
            {
                await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
            }
        }
    }
}
