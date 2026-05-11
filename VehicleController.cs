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
    private static volatile bool s_pendingLand    = false;
    private static volatile bool s_pendingHover   = false;
    private static Vector3       s_targetPos      = Vector3.Zero;
    private static volatile bool s_newTarget      = false;
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

                        if      (cmd == "SPAWN")   s_pendingSpawn   = true;
                        else if (cmd == "DESTROY")  s_pendingDestroy = true;
                        else if (cmd == "TAKEOFF")  s_pendingTakeoff = true;
                        else if (cmd == "LAND")     s_pendingLand    = true;
                        else if (cmd == "HOVER")    s_pendingHover   = true;
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
        if (s_pendingLand)    { s_pendingLand    = false; DoLand();    }
        if (s_pendingHover)   { s_pendingHover   = false; DoHover();   }

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

        // Exact hash from vAutoDrive source
        Function.Call(unchecked((Hash)(-2115941754365708377L)),
            _driver,
            _heli,
            0,
            target.X,
            target.Y,
            target.Z,
            4,
            speed,
            -1f,
            (float)_heli.Heading,
            -1
        );

        _driver.KeepTaskWhenMarkedAsNoLongerNeeded = true;
        ShowMessage(string.Format("Flying to ({0:F0},{1:F0},{2:F0}) spd:{3:F0}",
            target.X, target.Y, target.Z, speed));
    }

    private void DoTakeoff()
    {
        Vector3 takeoffTarget = new Vector3(_spawnPos.X, _spawnPos.Y, _spawnPos.Z + 80f);
        FlyTo(takeoffTarget, 30f);
        ShowMessage("Taking off...");
    }

    private void DoLand()
    {
        FlyTo(_spawnPos, 20f);
        ShowMessage("Landing...");
    }

    private void DoHover()
    {
        FlyTo(_heli.Position, 0f);
        ShowMessage("Hovering...");
    }

    private void SpawnHeli()
    {
        ShowMessage("Spawning...");
        DestroyHeli();

        Ped     player = Game.Player.Character;
        Vector3 spawn  = player.Position + new Vector3(0f, 0f, 5f);
        _spawnPos = spawn;

        Model m = new Model("frogger");
        m.Request(5000);
        if (!m.IsLoaded) { ShowMessage("Model FAILED"); return; }

        _heli = World.CreateVehicle(m, spawn, player.Heading);
        m.MarkAsNoLongerNeeded();

        if (_heli == null || !_heli.Exists()) { ShowMessage("Spawn FAILED"); return; }

        _heli.IsInvincible = true;
        Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _heli.Handle, true, true);
        Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _heli.Handle, true, true, false);

        _driver = _heli.CreatePedOnSeat(VehicleSeat.Driver, new Model(PedHash.FreemodeMale01));

        if (_driver == null || !_driver.Exists()) { ShowMessage("Driver FAILED"); return; }

        _driver.IsInvincible                     = true;
        _driver.BlockPermanentEvents              = true;
        _driver.KeepTaskWhenMarkedAsNoLongerNeeded = true;
        _driver.CanBeDraggedOutOfVehicle          = false;
        Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _driver.Handle, true, true);

        CreateCam();
        ShowMessage("Frogger ready! Python: takeoff to fly");
    }

    private void DestroyHeli()
    {
        DestroyCam();

        if (_driver != null && _driver.Exists())
        {
            _driver.Task.ClearAllImmediately();
            _driver.KeepTaskWhenMarkedAsNoLongerNeeded = false;
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
        try { s_teleSock.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, TELE_PORT)); }
        catch { }
    }

    private void CreateCam()
    {
        DestroyCam();
        _camHandle = Function.Call<int>(Hash.CREATE_CAM, "DEFAULT_SCRIPTED_CAMERA", false);
        Function.Call(Hash.ATTACH_CAM_TO_ENTITY, _camHandle, _heli.Handle, 0f, -12f, 4f, true);
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