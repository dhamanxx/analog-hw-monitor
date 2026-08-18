// Analog Hardware Monitor - Arduino UNO firmware.
//
// Reads frames of the form "V:a,b,c,d,e\n" where each value is 0-255, and writes
// them to five PWM pins. All scaling and calibration happen on the PC; this sketch
// deliberately knows nothing about temperatures or percentages.
//
// If no valid frame arrives for WATCHDOG_MS, every needle drops to zero, so a dead
// link never looks like an idle PC.

const uint8_t PINS[5] = {3, 5, 6, 9, 10};
const unsigned long WATCHDOG_MS = 3000;
const uint8_t BUFFER_SIZE = 32;

char buffer[BUFFER_SIZE];
uint8_t length = 0;
unsigned long lastFrameMs = 0;
bool zeroed = true;

void writeAll(uint8_t value) {
  for (uint8_t i = 0; i < 5; i++) {
    analogWrite(PINS[i], value);
  }
}

void handleLine() {
  if (buffer[0] != 'V' || buffer[1] != ':') {
    return;
  }

  int values[5];
  char* cursor = buffer + 2;

  for (uint8_t i = 0; i < 5; i++) {
    char* end;
    long value = strtol(cursor, &end, 10);
    if (end == cursor || value < 0 || value > 255) {
      return;
    }
    values[i] = (int)value;
    cursor = end;
    if (i < 4) {
      if (*cursor != ',') {
        return;
      }
      cursor++;
    }
  }

  if (*cursor != '\0') {
    return;
  }

  for (uint8_t i = 0; i < 5; i++) {
    analogWrite(PINS[i], values[i]);
  }
  lastFrameMs = millis();
  zeroed = false;
}

void setup() {
  for (uint8_t i = 0; i < 5; i++) {
    pinMode(PINS[i], OUTPUT);
  }
  writeAll(0);

  Serial.begin(115200);
  Serial.println("AHM1");
}

void loop() {
  while (Serial.available()) {
    char c = Serial.read();
    if (c == '\r') {
      continue;
    }
    if (c == '\n') {
      buffer[length] = '\0';
      handleLine();
      length = 0;
    } else if (length < BUFFER_SIZE - 1) {
      buffer[length++] = c;
    } else {
      length = 0;   // overlong line, drop it
    }
  }

  if (!zeroed && millis() - lastFrameMs > WATCHDOG_MS) {
    writeAll(0);
    zeroed = true;
  }
}
