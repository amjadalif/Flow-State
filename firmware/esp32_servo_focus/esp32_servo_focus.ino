/*
 * ESP32 Focus Score → Two Motors + Smiley Matrix + Burst Buttons
 * ===============================================================
 * Receives focus score (0–100) via USB Serial from Python.
 *
 * Score 0–33   (distracted) → motors STOP
 * Score 34–66  (neutral)    → motors SLOW
 * Score 67–100 (focused)    → motors BURST (fixed duration, counted)
 *
 * Also drives an 8x8 MAX7219 LED matrix showing a static smiley face.
 *
 * === Manual Motor Commands ===
 *   "M1W"          → Motor 1 wind
 *   "M1U"          → Motor 1 unwind
 *   "M2W"          → Motor 2 wind
 *   "M2U"          → Motor 2 unwind
 *   "MSTOP"        → Stop both motors
 *   "SHOME"        → Mark current position as home (resets burst counter)
 *   "SRETURN:N"    → Wind back N bursts (N sent from Python)
 *   "SRETURN"      → Wind back using local focusBurstCount (fallback)
 *
 * === Physical Buttons (each fires a BURST_MS burst on press) ===
 *   Button 1 (GPIO 4)  → Wind   Motor 1
 *   Button 2 (GPIO 13) → Unwind Motor 1
 *   Button 3 (GPIO 25) → Wind   Motor 2
 *   Button 4 (GPIO 26) → Unwind Motor 2
 *   Wiring: each button between its GPIO and GND (uses INPUT_PULLUP).
 *
 * Wiring (Motors) — Makerverse driver, both channels:
 *   Motor 1 (Channel A):
 *     Driver DIR A → GPIO 21     Driver A+ → Motor Pin 1
 *     Driver PWM A → GPIO 7      Driver A- → Motor Pin 2
 *     Encoder A    → GPIO 8
 *     Encoder B    → GPIO 37
 *
 *   Motor 2 (Channel B):
 *     Driver DIR B → GPIO 14     Driver B+ → Motor Pin 1
 *     Driver PWM B → GPIO 32     Driver B- → Motor Pin 2
 *     Encoder A    → GPIO 20
 *     Encoder B    → GPIO 22
 *
 * Wiring (MAX7219 8x8 LED Matrix):
 *   Matrix VCC -> Feather 3V
 *   Matrix GND -> Feather GND
 *   Matrix DIN -> Feather GPIO 15
 *   Matrix CS  -> Feather GPIO 27
 *   Matrix CLK -> Feather GPIO 33
 */



#include <LEDMatrixDriver.hpp>

const uint8_t LEDMATRIX_CS_PIN = 27;
LEDMatrixDriver lc(1, LEDMATRIX_CS_PIN);

// ============================================================
//  LED MATRIX
// ============================================================


// Smiley face pattern
byte smiley[8] = {
  B00111100,  // row 0:   ####
  B01000010,  // row 1:  #    #
  B10100101,  // row 2: # #  # #
  B10000001,  // row 3: #      #
  B10100101,  // row 4: # #  # #
  B10011001,  // row 5: #  ##  #
  B01000010,  // row 6:  #    #
  B00111100   // row 7:   ####
};

// ============================================================
//  MOTOR PINS
// ============================================================

const int DIR_M1    = 21;
const int PWM_M1    = 7;
const int ENC_A_M1  = 8;
const int ENC_B_M1  = 37;

const int DIR_M2    = 14;
const int PWM_M2    = 32;
const int ENC_A_M2  = 20;
const int ENC_B_M2  = 22;

// ============================================================
//  BUTTON PINS
// ============================================================

const int BTN_M1_WIND   = 4;
const int BTN_M1_UNWIND = 13;
const int BTN_M2_WIND   = 25;
const int BTN_M2_UNWIND = 26;

// ============================================================
//  TUNING
// ============================================================

const int BAUD_RATE      = 115200;
const int PWM_FREQ_HZ    = 1000;
const int PWM_RESOLUTION = 8;

const int SPEED_STOP     = 0;
const int SPEED_SLOW     = 100;
const int SPEED_FAST     = 200;
const int SPEED_MANUAL   = 185;
const int BUTTON_SPEED   = 255;   // burst speed for physical buttons

const bool DIR_WIND_M1   = HIGH;
const bool DIR_WIND_M2   = HIGH;

// ── Burst (focused state) ──
const unsigned long BURST_MS = 40;
const unsigned long BUTTON_BURST_MS = 40;   // burst duration for physical buttons
const unsigned long RETURN_BURST_MS = 600;
const unsigned long RETURN_GAP_MS   = 80;
const unsigned long RETURN_WIND_MS = 4000;   // total continuous wind time
const unsigned long RETURN_OPEN_MS  = 2000;   // time to fully open
const unsigned long RETURN_CLOSE_MS = 6000;   // time to fully close

// ── Button debounce ──
const unsigned long BTN_DEBOUNCE_MS = 30;

// ============================================================
//  BURST COUNTER — local fallback if Python sends plain SRETURN
// ============================================================

int focusBurstCount = 0;

// ============================================================
//  RETURN-TO-HOME STATE
// ============================================================

bool returningHome         = false;
int  returnBurstsRemaining = 0;
bool returnBurstActive     = false;
unsigned long returnBurstStartMs = 0;

// ============================================================
//  FOCUS SCORE STATE
// ============================================================

bool focusMotorsRunning = false;
long startTicksM1 = 0;
long startTicksM2 = 0;

// ── Burst state (per-motor, so both motors can burst independently) ──
bool burstActiveM1 = false;
unsigned long burstStartMsM1 = 0;
bool burstActiveM2 = false;
unsigned long burstStartMsM2 = 0;

// Legacy combined flag — kept for the focus-score path that bursts both
// motors at once; used by main loop to stop both when expired.
bool burstActive = false;
unsigned long burstStartMs = 0;

// ============================================================
//  BUTTON STATE
// ============================================================

int  btnLastState[4] = {HIGH, HIGH, HIGH, HIGH};   // released = HIGH (pull-up)
unsigned long btnLastChangeMs[4] = {0, 0, 0, 0};

// ============================================================
//  ENCODER STATE
// ============================================================

volatile long encoderTicks[2] = {0, 0};

void IRAM_ATTR isrM1() { encoderTicks[0] += digitalRead(DIR_M1) ? 1 : -1; }
void IRAM_ATTR isrM2() { encoderTicks[1] += digitalRead(DIR_M2) ? 1 : -1; }

// ============================================================
//  HELPERS
// ============================================================

void driveMotors(int speed) {
  digitalWrite(DIR_M1, DIR_WIND_M1);
  ledcWrite(PWM_M1, speed);
  digitalWrite(DIR_M2, DIR_WIND_M2);
  ledcWrite(PWM_M2, speed);
}

// Fire a single BURST_MS burst on Motor 1 in the given direction
void burstMotor1(bool windDirection) {
  digitalWrite(DIR_M1, windDirection ? HIGH : LOW);
  ledcWrite(PWM_M1, BUTTON_SPEED);
  burstActiveM1   = true;
  burstStartMsM1  = millis();
}

// Fire a single BURST_MS burst on Motor 2 in the given direction
void burstMotor2(bool windDirection) {
  digitalWrite(DIR_M2, windDirection ? HIGH : LOW);
  ledcWrite(PWM_M2, BUTTON_SPEED);
  burstActiveM2   = true;
  burstStartMsM2  = millis();
}

void applyFocusScore(float score) {
  if (score <= 33) {
    driveMotors(SPEED_STOP);
    focusMotorsRunning = false;
    Serial.print("Score: ");
    Serial.print(score, 1);
    Serial.println(" -> (distracted) | Motors: STOP");

  } else if (score <= 66) {
    if (!focusMotorsRunning) {
      startTicksM1 = encoderTicks[0];
      startTicksM2 = encoderTicks[1];
      focusMotorsRunning = true;
    }
    driveMotors(SPEED_SLOW);
    Serial.print("Score: ");
    Serial.print(score, 1);
    Serial.println(" -> (neutral) | Motors: SLOW");

  } else {
    digitalWrite(DIR_M1, !DIR_WIND_M1);
    ledcWrite(PWM_M1, SPEED_FAST);
    digitalWrite(DIR_M2, !DIR_WIND_M2);
    ledcWrite(PWM_M2, SPEED_FAST);
    burstActive  = true;
    burstStartMs = millis();
    focusBurstCount++;
    Serial.print("Score: ");
    Serial.print(score, 1);
    Serial.print(" -> (focused) | Motors: BURST #");
    Serial.println(focusBurstCount);
  }
}

// ============================================================
//  Manual Motor Control Helpers
// ============================================================

void driveMotor1(bool windDirection, int speed) {
  digitalWrite(DIR_M1, windDirection ? HIGH : LOW);
  ledcWrite(PWM_M1, speed);
}

void driveMotor2(bool windDirection, int speed) {
  digitalWrite(DIR_M2, windDirection ? HIGH : LOW);
  ledcWrite(PWM_M2, speed);
}

void stopAllMotors() {
  ledcWrite(PWM_M1, 0);
  ledcWrite(PWM_M2, 0);
}

bool handleManualCommand(String cmd) {
  if (cmd == "M1W") {
    driveMotor1(true, SPEED_MANUAL);
    Serial.println("CMD: Motor 1 WIND");
    return true;

  } else if (cmd == "M1U") {
    driveMotor1(false, SPEED_MANUAL);
    Serial.println("CMD: Motor 1 UNWIND");
    return true;

  } else if (cmd == "M2W") {
    driveMotor2(true, SPEED_MANUAL);
    Serial.println("CMD: Motor 2 WIND");
    return true;

  } else if (cmd == "M2U") {
    driveMotor2(false, SPEED_MANUAL);
    Serial.println("CMD: Motor 2 UNWIND");
    return true;

  } else if (cmd == "MSTOP") {
    stopAllMotors();
    returningHome = false;
    Serial.println("CMD: All motors STOPPED");
    return true;

  } else if (cmd == "SHOME") {
    focusBurstCount = 0;
    Serial.println("CMD: Home marked. Burst counter reset.");
    return true;

  } else if (cmd.startsWith("SRETURN")) {
    // Parse count from Python if provided (e.g. "SRETURN:55")
    // Fall back to local focusBurstCount if plain "SRETURN"
    int colon = cmd.indexOf(':');
    if (colon != -1) {
      returnBurstsRemaining = cmd.substring(colon + 1).toInt();
      Serial.print("CMD: SRETURN — count from Python: ");
      Serial.println(returnBurstsRemaining);
    } else {
      returnBurstsRemaining = focusBurstCount;
      Serial.print("CMD: SRETURN — count from local: ");
      Serial.println(returnBurstsRemaining);
    }

    if (returnBurstsRemaining == 0) {
      Serial.println("CMD: SRETURN — no bursts to undo.");
      return true;
    }

    returningHome      = true;
    returnBurstStartMs = millis();   // ← ADD THIS
    returnBurstActive  = false;
    focusMotorsRunning = false;
    burstActive        = false;
    Serial.print("CMD: Returning home — winding ");
    Serial.print(returnBurstsRemaining);
    Serial.println(" burst(s)...");
    return true;
  }

  return false;
}

// ============================================================
//  BUTTON HANDLING
// ============================================================

// Reads all 4 buttons; on a debounced HIGH→LOW edge (press), fires the
// matching burst. Buttons are ignored while a return-to-home is running
// so they don't fight the homing sequence.
void handleButtons() {
  if (returningHome) return;

  const int pins[4] = { BTN_M1_WIND, BTN_M1_UNWIND, BTN_M2_WIND, BTN_M2_UNWIND };

  for (int i = 0; i < 4; i++) {
    int reading = digitalRead(pins[i]);
    unsigned long now = millis();

    if (reading != btnLastState[i] &&
        (now - btnLastChangeMs[i]) >= BTN_DEBOUNCE_MS) {

      btnLastChangeMs[i] = now;

      // Detect a press (HIGH → LOW transition, since buttons pull to GND)
      if (btnLastState[i] == HIGH && reading == LOW) {
        switch (i) {
          case 0:  // BTN_M1_WIND
            burstMotor1(DIR_WIND_M1);
            Serial.println("BTN: Motor 1 WIND burst");
            break;
          case 1:  // BTN_M1_UNWIND
            burstMotor1(!DIR_WIND_M1);
            Serial.println("BTN: Motor 1 UNWIND burst");
            break;
          case 2:  // BTN_M2_WIND
            burstMotor2(DIR_WIND_M2);
            Serial.println("BTN: Motor 2 WIND burst");
            break;
          case 3:  // BTN_M2_UNWIND
            burstMotor2(!DIR_WIND_M2);
            Serial.println("BTN: Motor 2 UNWIND burst");
            break;
        }
      }

      btnLastState[i] = reading;
    }
  }
}

// ============================================================
//  SETUP
// ============================================================

void setup() {
  Serial.begin(BAUD_RATE);
  delay(500);
  Serial.println("Starting...");

  // ── LED Matrix init ──
  // Serial.println("Starting LED matrix...");
  // // Initialize: DIN=15, CLK=33, CS=27
  // lc.init(15, 33, 27);
  // // Wake it up from power-saving mode
  // lc.activateAllSegments();
  // // Set brightness (0=dim, 15=bright)
  // lc.setIntensity(2);
  // // Clear all LEDs first
  // lc.clearMatrix();
  // delay(100);
  // // Draw the smiley, one row at a time
  // for (int row = 0; row < 8; row++) {
  //   lc.setRow(0, row, smiley[row]);
  // }
  // Serial.println("Smiley drawn!");

  Serial.println("Starting LED matrix...");
lc.setEnabled(true);
lc.setIntensity(2);
lc.clear();
for (int row = 0; row < 8; row++) {
  for (int col = 0; col < 8; col++) {
    if (smiley[row] & (1 << (7 - col))) {
      lc.setPixel(col, row, true);
    }
  }
}
lc.display();
Serial.println("Smiley drawn!");

  // ── Motors ──
  pinMode(DIR_M1, OUTPUT);
  pinMode(DIR_M2, OUTPUT);
  digitalWrite(DIR_M1, LOW);
  digitalWrite(DIR_M2, LOW);

  ledcAttach(PWM_M1, PWM_FREQ_HZ, PWM_RESOLUTION);
  ledcAttach(PWM_M2, PWM_FREQ_HZ, PWM_RESOLUTION);
  ledcWrite(PWM_M1, 0);
  ledcWrite(PWM_M2, 0);

  pinMode(ENC_A_M1, INPUT_PULLUP);
  pinMode(ENC_B_M1, INPUT_PULLUP);
  pinMode(ENC_A_M2, INPUT_PULLUP);
  pinMode(ENC_B_M2, INPUT_PULLUP);

  attachInterrupt(digitalPinToInterrupt(ENC_A_M1), isrM1, RISING);
  attachInterrupt(digitalPinToInterrupt(ENC_A_M2), isrM2, RISING);

  // ── Buttons ──
  pinMode(BTN_M1_WIND,   INPUT_PULLUP);
  pinMode(BTN_M1_UNWIND, INPUT_PULLUP);
  pinMode(BTN_M2_WIND,   INPUT_PULLUP);
  pinMode(BTN_M2_UNWIND, INPUT_PULLUP);

  Serial.println("ESP32 Motors + Matrix + Buttons ready!");
}

// ============================================================
//  MAIN LOOP
// ============================================================

void loop() {
  // ── Read buttons ──
  handleButtons();

  // ── Per-motor button-burst auto-stop ──
  if (burstActiveM1 && (millis() - burstStartMsM1 >= BUTTON_BURST_MS)) {
    ledcWrite(PWM_M1, 0);
    burstActiveM1 = false;
  }
  if (burstActiveM2 && (millis() - burstStartMsM2 >= BUTTON_BURST_MS)) {
    ledcWrite(PWM_M2, 0);
    burstActiveM2 = false;
  }

  // ── Focused unwind burst auto-stop ──
  if (burstActive && (millis() - burstStartMs >= BURST_MS)) {
    ledcWrite(PWM_M1, 0);
    ledcWrite(PWM_M2, 0);
    burstActive = false;
  }

  // ── Return-to-home: wind continuously for a fixed time ──
  if (returningHome) {
    unsigned long elapsed = millis() - returnBurstStartMs;

    if (elapsed < RETURN_OPEN_MS) {
      // Phase 1: OPEN
      digitalWrite(DIR_M1, !DIR_WIND_M1);
      ledcWrite(PWM_M1, SPEED_MANUAL);
      digitalWrite(DIR_M2, !DIR_WIND_M2);
      ledcWrite(PWM_M2, SPEED_MANUAL);
    } else if (elapsed < RETURN_OPEN_MS + 500) {
      // Pause: let string go slack
      stopAllMotors();
    } else if (elapsed < RETURN_OPEN_MS + 500 + RETURN_CLOSE_MS) {
      // Phase 2: CLOSE with full-power kick to break free
      unsigned long closeElapsed = elapsed - (RETURN_OPEN_MS + 500);
      int spd = (closeElapsed < 400) ? 255 : SPEED_MANUAL;
      digitalWrite(DIR_M1, DIR_WIND_M1);
      ledcWrite(PWM_M1, spd);
      digitalWrite(DIR_M2, DIR_WIND_M2);
      ledcWrite(PWM_M2, spd);
    } else {
      stopAllMotors();
      returningHome   = false;
      focusBurstCount = 0;
      Serial.println("Home reached.");
    }
  }

  if (Serial.available() > 0) {
    String incoming = Serial.readStringUntil('\n');
    incoming.trim();

    if (incoming.length() == 0) return;

    if (!handleManualCommand(incoming)) {
      float score = incoming.toFloat();
      applyFocusScore(score);
    }
  }

  delay(20);
}
