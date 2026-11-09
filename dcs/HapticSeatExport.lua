-- HapticSeat: envía la telemetría de DCS por UDP a la app del cojín (127.0.0.1:5679, ~60 veces por segundo).
-- Se instala en Saved Games\DCS.openbeta\Scripts\HapticSeat\ y se carga desde Export.lua con:
--   pcall(dofile, lfs.writedir() .. [[Scripts\HapticSeat\HapticSeatExport.lua]])
-- Encadena las funciones LuaExport* previas y nunca deja salir un error: no debe molestar a SimAppPro,
-- MOZA ni DCS-BIOS. Si la app no está abierta, los paquetes simplemente se pierden.
--
-- Formato (texto, separado por ";"; un campo vacío = sin dato):
--   HS1;tiempo;avión;ias;agl;g;flaps;aerofreno;tren;rodMorro;rodIzq;rodDer;rpmIzq;rpmDer;
--   proyectiles;bombas;cohetes;averías;aviso de pérdida

do
    local HOST, PORT = "127.0.0.1", 5679
    local SEND_INTERVAL = 1 / 60
    local SLOW_INTERVAL = 0.1 -- armamento y averías: no hace falta leerlos en cada fotograma

    package.path = package.path .. ";.\\LuaSocket\\?.lua"
    package.cpath = package.cpath .. ";.\\LuaSocket\\?.dll"
    local socket = require("socket")

    local udp
    local failed = false
    local nextSend, nextSlow = 0, 0
    local shells, bombs, rockets, failures, stall = "", "", "", "", 0

    local WEAPON, MISSILE, BOMB, NURS = 4, 4, 5, 7

    local function call(fn)
        if not fn then return nil end
        local ok, result = pcall(fn)
        if ok then return result end
        return nil
    end

    local function num(v)
        if type(v) ~= "number" or v ~= v then return "" end
        return string.format("%.3f", v)
    end

    local function readSlow()
        local payload = call(LoGetPayloadInfo)
        if type(payload) == "table" then
            shells = type(payload.Cannon) == "table" and payload.Cannon.shells or ""
            local b, r = 0, 0
            for _, station in pairs(payload.Stations or {}) do
                local w = station.weapon
                if type(w) == "table" and w.level1 == WEAPON then
                    if w.level2 == BOMB then b = b + (station.count or 0)
                    elseif w.level2 == MISSILE or w.level2 == NURS then r = r + (station.count or 0) end
                end
            end
            bombs, rockets = b, r
        end

        local mcp = call(LoGetMCPState)
        if type(mcp) == "table" then
            local count = 0
            for key, value in pairs(mcp) do
                if value == true and (string.find(key, "Failure", 1, true) or key == "FuelTankDamage") then
                    count = count + 1
                end
            end
            failures = count
            stall = mcp.StallSignalization == true and 1 or 0
        end
    end

    local function send()
        local now = socket.gettime()
        if now < nextSend then return end
        nextSend = now + SEND_INTERVAL

        -- Sin permiso del servidor no se envía nada: la app lo ve como una pausa
        if LoIsOwnshipExportAllowed and not LoIsOwnshipExportAllowed() then return end

        if now >= nextSlow then
            nextSlow = now + SLOW_INTERVAL
            readSlow()
        end

        local self = call(LoGetSelfData)
        local accel = call(LoGetAccelerationUnits)
        local mech = call(LoGetMechInfo)
        local engine = call(LoGetEngineInfo)

        local gear = type(mech) == "table" and mech.gear or nil
        local main = type(gear) == "table" and gear.main or nil
        local rpm = type(engine) == "table" and engine.RPM or nil

        udp:send(table.concat({
            "HS1",
            num(call(LoGetModelTime)),
            type(self) == "table" and self.Name or "",
            num(call(LoGetIndicatedAirSpeed)),
            num(call(LoGetAltitudeAboveGroundLevel)),
            type(accel) == "table" and num(accel.y) or "",
            type(mech) == "table" and type(mech.flaps) == "table" and num(mech.flaps.value) or "",
            type(mech) == "table" and type(mech.speedbrakes) == "table" and num(mech.speedbrakes.value) or "",
            type(gear) == "table" and num(gear.value) or "",
            type(main) == "table" and type(main.nose) == "table" and num(main.nose.rod) or "",
            type(main) == "table" and type(main.left) == "table" and num(main.left.rod) or "",
            type(main) == "table" and type(main.right) == "table" and num(main.right.rod) or "",
            type(rpm) == "table" and num(rpm.left) or "",
            type(rpm) == "table" and num(rpm.right) or "",
            tostring(shells), tostring(bombs), tostring(rockets), tostring(failures), tostring(stall),
        }, ";"))
    end

    local function guarded(fn)
        if failed then return end
        local ok, err = pcall(fn)
        if not ok then
            failed = true -- un error por fotograma llenaría el registro: se avisa una vez y se deja de enviar
            if log and log.write then log.write("HapticSeat", log.ERROR, tostring(err)) end
        end
    end

    local prevStart = LuaExportStart
    local prevStop = LuaExportStop
    local prevAfter = LuaExportAfterNextFrame

    LuaExportStart = function()
        guarded(function()
            udp = socket.udp()
            udp:settimeout(0)
            udp:setpeername(HOST, PORT)
        end)
        if prevStart then prevStart() end
    end

    LuaExportAfterNextFrame = function()
        if udp then guarded(send) end
        if prevAfter then prevAfter() end
    end

    LuaExportStop = function()
        guarded(function()
            if udp then udp:close() end
            udp = nil
        end)
        if prevStop then prevStop() end
    end
end
