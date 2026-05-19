using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

public class VehicleController : Script
{
    private const int TELE_PORT = 55000;
    private const int CMD_PORT  = 55001;

    private static UdpClient s_teleSock  = null;
    private static Thread    s_cmdThread = null;
    private static bool      s_socketsOk = false;

    private static readonly object s_lock = new object();

    private static volatile bool s_pendingSpawn   = false;
    private static volatile bool s_pendingDestroy = false;
    private static volatile bool s_pendingTakeoff = false;
    private static volatile bool s_pendingHover   = false;
    private static Vector3       s_targetPos      = Vector3.Zero;
    private static volatile bool s_newTarget      = false;
    private static volatile bool s_pendingLand    = false;
    private static Vector3       s_landTarget     = Vector3.Zero;
    private static float         s_targetSpeed    = 30f;

    private Vehicle _heli      = null;
    private Ped     _driver    = null;
    private int     _camHandle = -1;
    private bool    _camActive = false;
    private Vector3 _spawnPos  = Vector3.Zero;

    public VehicleController()
    {
        Tick    += OnTick;
        KeyDown += OnKeyDown;

        if (!s_socketsOk)
            s_socketsOk = OpenSockets();

        ShowMessage(s_socketsOk
            ? "GTASim ready — F5 Spawn | F6 Cam | F7 Destroy"
            : "GTASim: socket error");
    }

    private static bool OpenSockets()
    {
        try { s_teleSock = new UdpClient(); }
        catch (Exception ex) { ShowMessage("Tele FAILED: " + ex.Message); return false; }

        try
        {
            var cmdSock = new UdpClient();
            cmdSock.Client.SetSocketOption(SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress, true);
            cmdSock.Client.SetSocketOption(SocketOptionLevel.Socket,
                SocketOptionName.ExclusiveAddressUse, false);
            cmdSock.Client.Bind(new IPEndPoint(IPAddress.Any, CMD_PORT));

            s_cmdThread = new Thread(() =>
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    try
                    {
                        byte[] data = cmdSock.Receive(ref ep);
                        string cmd  = Encoding.UTF8.GetString(data).Trim();

                        if (cmd == "SPAWN")
                            s_pendingSpawn = true;
                        else if (cmd == "DESTROY")
                            s_pendingDestroy = true;
                        else if (cmd == "TAKEOFF")
                            s_pendingTakeoff = true;
                        else if (cmd == "HOVER")
                            s_pendingHover = true;
                        else if (cmd.StartsWith("LAND,"))
                        {
                            // LAND,x,y,z  or  LAND,HERE  or  LAND,SPAWN
                            string[] p = cmd.Split(',');
                            lock (s_lock)
                            {
                                if (p.Length == 2 && p[1] == "HERE")
                                {
                                    s_landTarget = Vector3.Zero; // signal = use current pos
                                    s_pendingLand = true;
                                }
                                else if (p.Length == 2 && p[1] == "SPAWN")
                                {
                                    s_landTarget = new Vector3(-1, -1, -1); // signal = use spawn
                                    s_pendingLand = true;
                                }
                                else if (p.Length == 4)
                                {
                                    float x, y, z;
                                    if (float.TryParse(p[1], out x) &&
                                        float.TryParse(p[2], out y) &&
                                        float.TryParse(p[3], out z))
                                    {
                                        s_landTarget = new Vector3(x, y, z);
                                        s_pendingLand = true;
                                    }
                                }
                            }
                        }
                        else if (cmd.StartsWith("GOTO,"))
                        {
                            string[] p = cmd.Split(',');
                            if (p.Length >= 4)
                            {
                                float x, y, z;
                                if (float.TryParse(p[1], out x) &&
                                    float.TryParse(p[2], out y) &&
                                    float.TryParse(p[3], out z))
                                {
                                    lock (s_lock)
                                    {
                                        s_targetPos = new Vector3(x, y, z);
                                        if (p.Length >= 5)
                                            float.TryParse(p[4], out s_targetSpeed);
                                        s_newTarget = true;
                                    }
                                }
                            }
                        }
                        else if (cmd.StartsWith("SPEED,"))
                        {
                            string[] p = cmd.Split(',');
                            if (p.Length == 2)
                                lock (s_lock) float.TryParse(p[1], out s_targetSpeed);
                        }
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "GTASim-CMD" };
            s_cmdThread.Start();
        }
        catch (Exception ex) { ShowMessage("CMD FAILED: " + ex.Message); return false; }

        return true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F5: SpawnHeli();   break;
            case Keys.F6: ToggleCam();   break;
            case Keys.F7: DestroyHeli(); break;
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (s_pendingSpawn)   { s_pendingSpawn   = false; SpawnHeli();   }
        if (s_pendingDestroy) { s_pendingDestroy  = false; DestroyHeli(); }

        if (_heli == null || !_heli.Exists()) return;

        if (s_pendingTakeoff) { s_pendingTakeoff = false; DoTakeoff(); }
        if (s_pendingHover)   { s_pendingHover   = false; DoHover();   }

        if (s_pendingLand)
        {
            s_pendingLand = false;
            Vector3 lt;
            lock (s_lock) { lt = s_landTarget; }
            DoLand(lt);
        }

        if (s_newTarget)
        {
            s_newTarget = false;
            Vector3 t; float spd;
            lock (s_lock) { t = s_targetPos; spd = s_targetSpeed; }
            FlyTo(t, spd);
        }

        SendPythonTelemetry();
    }

    private void FlyTo(Vector3 target, float speed)
    {
        if (_driver == null || !_driver.Exists()) return;

        _driver.Task.ClearAll();

        Function.Call(unchecked((Hash)(-2115941754365708377L)),
            _driver,
            _heli,
            target.X,
            target.Y,
            (int)target.Z,
            speed,
            1,
            (float)(uint)_heli.Model.GetHashCode(),
            1,
            5f,
            -1
        );

        Function.Call(Hash.SET_PED_KEEP_TASK, _driver.Handle, true);
        ShowMessage(string.Format("Flying to ({0:F0},{1:F0},{2:F0})",
            target.X, target.Y, target.Z));
    }

    private void DoTakeoff()
    {
        if (_driver == null || !_driver.Exists()) return;

        float cx = _heli.Position.X;
        float cy = _heli.Position.Y;
        float cz = _heli.Position.Z;

        _spawnPos = new Vector3(cx, cy, cz);
        FlyTo(new Vector3(cx, cy, cz + 60f), 20f);
        ShowMessage(string.Format("Taking off to Z={0:F0}", cz + 60f));
    }

   private void DoLand(Vector3 landTarget)
    {
        if (_driver == null || !_driver.Exists()) return;

        Vector3 target;

        if (landTarget == Vector3.Zero)
        {
            target = new Vector3(_heli.Position.X, _heli.Position.Y, _heli.Position.Z);
            ShowMessage("Landing here...");
        }
        else if (landTarget.X == -1 && landTarget.Y == -1 && landTarget.Z == -1)
        {
            target = new Vector3(_spawnPos.X, _spawnPos.Y, _spawnPos.Z);
            ShowMessage("Returning to spawn...");
        }
        else
        {
            target = new Vector3(landTarget.X, landTarget.Y, landTarget.Z);
            ShowMessage(string.Format("Landing at ({0:F0},{1:F0})", target.X, target.Y));
        }

        // Step 1: fly to XY of target at low altitude
        _driver.Task.ClearAll();
        Function.Call(unchecked((Hash)(-2115941754365708377L)),
            _driver, _heli,
            target.X, target.Y,
            (int)(target.Z + 5f),   // just 5m above ground
            15f, 1,
            (float)(uint)_heli.Model.GetHashCode(),
            1, 3f, -1
        );
        Function.Call(Hash.SET_PED_KEEP_TASK, _driver.Handle, true);

        // Step 2: after 4 seconds start forcing it down
        var landThread = new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(4000);
            // Force descend by setting velocity downward every tick for 10 seconds
            var endTime = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < endTime)
            {
                lock (s_lock)
                {
                    if (_heli != null && _heli.Exists())
                        _heli.Velocity = new GTA.Math.Vector3(0f, 0f, -5f);
                }
                System.Threading.Thread.Sleep(100);
            }
        }) { IsBackground = true };
        landThread.Start();
    }

    private void DoHover()
    {
        if (_driver == null || !_driver.Exists()) return;
        FlyTo(new Vector3(_heli.Position.X, _heli.Position.Y, _heli.Position.Z), 5f);
        ShowMessage("Hovering in place");
    }

    private void SpawnHeli()
    {
        ShowMessage("Spawning...");
        DestroyHeli();

        Ped     player = Game.Player.Character;
        Vector3 spawn  = player.Position + new Vector3(0f, 0f, 3f);

        Model m = new Model("frogger");
        m.Request(5000);
        if (!m.IsLoaded) { ShowMessage("Model FAILED"); return; }

        _heli = World.CreateVehicle(m, spawn, player.Heading);
        m.MarkAsNoLongerNeeded();

        if (_heli == null || !_heli.Exists()) { ShowMessage("Spawn FAILED"); return; }

        _spawnPos = _heli.Position;

        _heli.IsInvincible = true;
        Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _heli.Handle, true, true);
        Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _heli.Handle, true, true, false);

        _driver = _heli.CreatePedOnSeat(VehicleSeat.Driver,
            new Model(PedHash.FreemodeMale01));
        if (_driver == null || !_driver.Exists()) { ShowMessage("Driver FAILED"); return; }

        _driver.IsInvincible             = true;
        _driver.BlockPermanentEvents     = true;
        _driver.CanBeDraggedOutOfVehicle = false;
        Function.Call(Hash.SET_PED_KEEP_TASK, _driver.Handle, true);
        Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _driver.Handle, true, true);

        int seats = Function.Call<int>(
            Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, _heli.Handle);
        if (seats >= 1)
        {
            player.Task.WarpIntoVehicle(_heli, VehicleSeat.Passenger);
            ShowMessage("Franklin in passenger seat!");
        }

        CreateCam();
        ShowMessage("Frogger ready! Python: takeoff to fly");
    }

    private void DestroyHeli()
    {
        DestroyCam();

        Ped player = Game.Player.Character;
        if (player != null && _heli != null &&
            _heli.Exists() && player.IsInVehicle(_heli))
        {
            player.Task.LeaveVehicle();
            Script.Wait(200);
        }

        if (_driver != null && _driver.Exists())
        {
            _driver.Task.ClearAllImmediately();
            Function.Call(Hash.SET_PED_KEEP_TASK, _driver.Handle, false);
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _driver.Handle, false, true);
            _driver.MarkAsNoLongerNeeded();
            _driver.Delete();
            _driver = null;
        }

        if (_heli != null && _heli.Exists())
        {
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _heli.Handle, false, true);
            _heli.Delete();
        }
        _heli = null;
    }

    private void SendPythonTelemetry()
    {
        if (s_teleSock == null || _heli == null || !_heli.Exists()) return;
        Vector3 pos = _heli.Position;
        Vector3 vel = _heli.Velocity;
        string msg = string.Format("{0},{1},{2},{3},{4},{5}",
            pos.X, pos.Y, pos.Z, vel.X, vel.Y, vel.Z);
        byte[] data = Encoding.UTF8.GetBytes(msg);
        try
        {
            s_teleSock.Send(data, data.Length,
                new IPEndPoint(IPAddress.Loopback, TELE_PORT));
        }
        catch { }
    }

    private void CreateCam()
    {
        DestroyCam();
        _camHandle = Function.Call<int>(
            Hash.CREATE_CAM, "DEFAULT_SCRIPTED_CAMERA", false);
        Function.Call(Hash.ATTACH_CAM_TO_ENTITY,
            _camHandle, _heli.Handle, 0f, -12f, 4f, true);
        Function.Call(Hash.SET_CAM_ROT,    _camHandle, -15f, 0f, 0f, 2);
        Function.Call(Hash.SET_CAM_ACTIVE, _camHandle, true);
        Function.Call(Hash.RENDER_SCRIPT_CAMS, true, false, 0, true, false);
        _camActive = true;
    }

    private void DestroyCam()
    {
        if (_camHandle != -1)
        {
            Function.Call(Hash.RENDER_SCRIPT_CAMS, false, false, 0, true, false);
            Function.Call(Hash.SET_CAM_ACTIVE,     _camHandle, false);
            Function.Call(Hash.DESTROY_CAM,        _camHandle, false);
            _camHandle = -1;
        }
        _camActive = false;
    }

    private void ToggleCam()
    {
        if (_camHandle == -1) return;
        _camActive = !_camActive;
        Function.Call(Hash.RENDER_SCRIPT_CAMS, _camActive, false, 0, true, false);
    }

    private static void ShowMessage(string msg)
    {
        GTA.UI.Notification.PostTicker(msg, false);
    }
}