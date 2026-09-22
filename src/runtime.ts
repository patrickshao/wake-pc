export const errorMessage = (error: unknown): string => {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string" && error) return error;
  if (error && typeof error === "object") {
    const details = error as Record<string, unknown>;
    for (const key of ["message", "error", "detail"]) {
      if (typeof details[key] === "string" && details[key]) return details[key];
    }
    try {
      return JSON.stringify(error);
    } catch {
      // Fall through to the stable user-facing fallback.
    }
  }
  return "Decky backend unavailable";
};

export const delay = (milliseconds: number): Promise<void> =>
  new Promise((resolve) => globalThis.setTimeout(resolve, milliseconds));

export const withTimeout = <Value,>(
  promise: Promise<Value>,
  milliseconds: number,
  message: string,
): Promise<Value> =>
  new Promise((resolve, reject) => {
    const timeout = globalThis.setTimeout(
      () => reject(new Error(message)),
      milliseconds,
    );
    void promise.then(
      (value) => {
        globalThis.clearTimeout(timeout);
        resolve(value);
      },
      (error: unknown) => {
        globalThis.clearTimeout(timeout);
        reject(error);
      },
    );
  });

export const retryWithTimeout = async <Value,>(
  operation: () => Promise<Value>,
  attempts: number,
  timeoutMilliseconds: number,
  retryDelayMilliseconds: number,
  timeoutMessage: string,
): Promise<Value> => {
  if (attempts < 1) throw new Error("At least one attempt is required");

  let lastError: unknown = new Error(timeoutMessage);
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    try {
      return await withTimeout(
        operation(),
        timeoutMilliseconds,
        timeoutMessage,
      );
    } catch (error: unknown) {
      lastError = error;
      if (attempt + 1 < attempts) await delay(retryDelayMilliseconds);
    }
  }
  throw lastError;
};
