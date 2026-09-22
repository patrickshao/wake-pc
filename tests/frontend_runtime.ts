import { errorMessage, retryWithTimeout, withTimeout } from "../src/runtime.js";

declare const process: { exitCode: number };

const assert = (condition: unknown, message: string): void => {
  if (!condition) throw new Error(message);
};

const main = async (): Promise<void> => {
  assert(errorMessage(new Error("backend broke")) === "backend broke", "Error messages should survive");
  assert(errorMessage({ detail: "details" }) === "details", "Structured Decky errors should be readable");
  assert(errorMessage(undefined) === "Decky backend unavailable", "Unknown errors need a stable fallback");

  let attempts = 0;
  const value = await retryWithTimeout(
    async () => {
      attempts += 1;
      if (attempts < 3) throw new Error("not ready");
      return "loaded";
    },
    3,
    50,
    0,
    "timed out",
  );
  assert(value === "loaded", "Retry should return the successful result");
  assert(attempts === 3, "Retry should use the configured attempt count");

  let timeoutMessage = "";
  try {
    await withTimeout(new Promise<string>(() => undefined), 5, "backend timeout");
  } catch (error: unknown) {
    timeoutMessage = errorMessage(error);
  }
  assert(timeoutMessage === "backend timeout", "Hung RPC calls should time out clearly");

  console.log("Frontend runtime tests passed");
};

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
