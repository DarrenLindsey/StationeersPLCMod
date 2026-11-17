namespace StationeersPlc
{
    /// <summary>
    /// Built-in function blocks, written in the same Structured Text the player uses.
    /// Each block is copied (inlined) into the program wherever an instance is called, so a block is just
    /// ordinary ST with its own private variables. Players can read this text as worked examples and write their own
    /// with FUNCTION_BLOCK ... END_FUNCTION_BLOCK.
    ///
    /// Units in Stationeers: pressure in kPa, temperature in Kelvin, angles in degrees, time in T#... literals.
    /// DT() is the real time since the previous scan in milliseconds.
    /// Do not use double quotes in this text (it is a C# verbatim string).
    /// </summary>
    internal static class StLibrary
    {
        public const string Source = @"
(* ---------- Generic blocks ---------- *)

FUNCTION_BLOCK R_TRIG   (* Q is true for ONE scan when CLK goes from off to on *)
VAR_INPUT CLK : BOOL; END_VAR
VAR_OUTPUT Q : BOOL; END_VAR
VAR M : BOOL; END_VAR
Q := CLK AND NOT M;
M := CLK;
END_FUNCTION_BLOCK

FUNCTION_BLOCK F_TRIG   (* Q is true for ONE scan when CLK goes from on to off *)
VAR_INPUT CLK : BOOL; END_VAR
VAR_OUTPUT Q : BOOL; END_VAR
VAR M : BOOL; END_VAR
Q := NOT CLK AND M;
M := CLK;
END_FUNCTION_BLOCK

FUNCTION_BLOCK SR   (* Latch. Set turns Q1 on, Reset turns it off. Set wins if both are on *)
VAR_INPUT S1 : BOOL; R : BOOL; END_VAR
VAR_OUTPUT Q1 : BOOL; END_VAR
Q1 := S1 OR (Q1 AND NOT R);
END_FUNCTION_BLOCK

FUNCTION_BLOCK RS   (* Latch. Set turns Q1 on, Reset turns it off. Reset wins if both are on *)
VAR_INPUT S : BOOL; R1 : BOOL; END_VAR
VAR_OUTPUT Q1 : BOOL; END_VAR
Q1 := NOT R1 AND (S OR Q1);
END_FUNCTION_BLOCK

FUNCTION_BLOCK TOF   (* Off-delay timer. Q follows IN, but stays on for PT after IN turns off *)
VAR_INPUT IN : BOOL; PT : TIME := T#1s; END_VAR
VAR_OUTPUT Q : BOOL; END_VAR
VAR T : TON; Armed : BOOL; END_VAR
T(IN := Armed AND NOT IN, PT := PT);
IF IN THEN
    Armed := TRUE;
END_IF;
IF T.Q THEN
    Armed := FALSE;
END_IF;
Q := IN OR Armed;
END_FUNCTION_BLOCK

FUNCTION_BLOCK CTU   (* Up counter. CV counts rising edges of CU. Q is true when CV >= PV. R resets *)
VAR_INPUT CU : BOOL; R : BOOL; PV : INT := 10; END_VAR
VAR_OUTPUT Q : BOOL; CV : INT; END_VAR
VAR M : BOOL; END_VAR
IF R THEN
    CV := 0;
ELSIF CU AND NOT M THEN
    CV := CV + 1;
END_IF;
M := CU;
Q := CV >= PV;
END_FUNCTION_BLOCK

FUNCTION_BLOCK BLINK   (* Flashes Q while ENABLE is on. Good for alarms and warning lights *)
VAR_INPUT ENABLE : BOOL; ON_TIME : TIME := T#500ms; OFF_TIME : TIME := T#500ms; END_VAR
VAR_OUTPUT Q : BOOL; END_VAR
VAR TA : TON; TB : TON; END_VAR
TA(IN := ENABLE AND NOT Q, PT := OFF_TIME);
TB(IN := ENABLE AND Q, PT := ON_TIME);
IF NOT ENABLE THEN
    Q := FALSE;
ELSIF TA.Q THEN
    Q := TRUE;
ELSIF TB.Q THEN
    Q := FALSE;
END_IF;
END_FUNCTION_BLOCK

(* ---------- Process control ---------- *)

FUNCTION_BLOCK HYST   (* Two-limit switch with a gap. Q turns on at or below LOW and off at or above HIGH.
                         INVERT := TRUE flips it: on at or above HIGH, off at or below LOW (coolers, vents). *)
VAR_INPUT IN : REAL; LOW : REAL; HIGH : REAL; INVERT : BOOL; END_VAR
VAR_OUTPUT Q : BOOL; END_VAR
VAR State : BOOL; END_VAR
IF IN <= LOW THEN
    State := TRUE;
ELSIF IN >= HIGH THEN
    State := FALSE;
END_IF;
Q := State XOR INVERT;
END_FUNCTION_BLOCK

FUNCTION_BLOCK PID   (* PID controller. KI is per second, KD is seconds. Output is limited to OUT_MIN..OUT_MAX
                        and the integral is held inside the same limits (anti-windup). ENABLE off resets it. *)
VAR_INPUT ENABLE : BOOL := TRUE; PV : REAL; SP : REAL; KP : REAL := 1.0; KI : REAL; KD : REAL;
          OUT_MIN : REAL := 0.0; OUT_MAX : REAL := 100.0; END_VAR
VAR_OUTPUT OUT : REAL; END_VAR
VAR E : REAL; PrevE : REAL; Integ : REAL; Secs : REAL; Deriv : REAL; Fresh : BOOL := TRUE; END_VAR
Secs := DT() / 1000.0;
IF ENABLE THEN
    E := SP - PV;
    Integ := LIMIT(OUT_MIN, Integ + KI * E * Secs, OUT_MAX);
    Deriv := 0.0;
    IF Secs > 0.0 AND NOT Fresh THEN
        Deriv := KD * (E - PrevE) / Secs;
    END_IF;
    OUT := LIMIT(OUT_MIN, KP * E + Integ + Deriv, OUT_MAX);
    PrevE := E;
    Fresh := FALSE;
ELSE
    Integ := 0.0;
    OUT := OUT_MIN;
    Fresh := TRUE;
END_IF;
END_FUNCTION_BLOCK

(* ---------- Stationeers blocks ---------- *)

FUNCTION_BLOCK SOLAR_TRACK   (* Turns a solar sensor reading into panel settings.
                                Horizontal passes straight through. Vertical is 90 - sensor (MODE 0) or sensor + 90 (MODE 1),
                                the two forms in the Stationeers wiki solar guide. Pick the one that matches how the sensor is mounted. *)
VAR_INPUT SENSOR_H : REAL; SENSOR_V : REAL; MODE : INT; END_VAR
VAR_OUTPUT PANEL_H : REAL; PANEL_V : REAL; END_VAR
PANEL_H := SENSOR_H;
IF MODE = 1 THEN
    PANEL_V := SENSOR_V + 90.0;
ELSE
    PANEL_V := 90.0 - SENSOR_V;
END_IF;
END_FUNCTION_BLOCK

FUNCTION_BLOCK AIRLOCK   (* Two-door airlock cycle with pressure checks, door locks and a safety timeout.
   Pulse GO_IN to go to the station side, GO_OUT to go to the outside. The block closes and locks both doors,
   pumps the chamber to the target pressure (FILL adds gas from the station, EVAC removes it), then opens only
   the destination door. If the pressure is not reached before TIMEOUT it stops with FAULT on and both doors
   locked. Pulse RESET to clear a fault. STATE: 0 idle, 1 closing, 2 equalising, 3 opening, 9 fault. *)
VAR_INPUT
    GO_IN : BOOL; GO_OUT : BOOL; RESET : BOOL;
    PRESSURE : REAL;
    TARGET_IN : REAL := 100.0; TARGET_OUT : REAL := 0.0; TOL : REAL := 2.0;
    TIMEOUT : TIME := T#60s;
END_VAR
VAR_OUTPUT
    OPEN_IN : BOOL; OPEN_OUT : BOOL; LOCK_IN : BOOL; LOCK_OUT : BOOL;
    FILL : BOOL; EVAC : BOOL; BUSY : BOOL; FAULT : BOOL; STATE : INT;
END_VAR
VAR Goal : REAL; ToInside : BOOL; MIn : BOOL; MOut : BOOL; Settle : TON; Limit : TON; END_VAR
Settle(IN := STATE = 1, PT := T#1500ms);
Limit(IN := STATE = 2, PT := TIMEOUT);
IF STATE = 0 THEN
    IF GO_IN AND NOT MIn THEN
        ToInside := TRUE;
        Goal := TARGET_IN;
        STATE := 1;
    ELSIF GO_OUT AND NOT MOut THEN
        ToInside := FALSE;
        Goal := TARGET_OUT;
        STATE := 1;
    END_IF;
ELSIF STATE = 1 THEN
    IF Settle.Q THEN
        STATE := 2;
    END_IF;
ELSIF STATE = 2 THEN
    FILL := PRESSURE < Goal - TOL;
    EVAC := PRESSURE > Goal + TOL;
    IF NOT FILL AND NOT EVAC THEN
        STATE := 3;
    ELSIF Limit.Q THEN
        STATE := 9;
    END_IF;
ELSIF STATE = 3 THEN
    OPEN_IN := ToInside; OPEN_OUT := NOT ToInside;
    LOCK_IN := NOT ToInside; LOCK_OUT := ToInside;
    STATE := 0;
ELSIF STATE = 9 THEN
    IF RESET THEN
        STATE := 0;
    END_IF;
END_IF;
(* Safe outputs for the state we are now in: both doors shut and locked while cycling or faulted, pumps only while equalising *)
IF STATE = 1 OR STATE = 2 OR STATE = 9 THEN
    OPEN_IN := FALSE; OPEN_OUT := FALSE; LOCK_IN := TRUE; LOCK_OUT := TRUE;
END_IF;
IF STATE <> 2 THEN
    FILL := FALSE; EVAC := FALSE;
END_IF;
MIn := GO_IN;
MOut := GO_OUT;
BUSY := STATE = 1 OR STATE = 2 OR STATE = 3;
FAULT := STATE = 9;
END_FUNCTION_BLOCK
";
    }
}
