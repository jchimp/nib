// A tiny retrying fetch wrapper. No dependencies, no framework.

const DEFAULTS = {
  retries: 3,
  backoffMs: 250,
  timeoutMs: 10_000,
  retryOn: [408, 429, 500, 502, 503, 504],
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

class HttpError extends Error {
  constructor(response, body) {
    super(`${response.status} ${response.statusText}`);
    this.name = "HttpError";
    this.status = response.status;
    this.body = body;
  }

  get retryable() {
    return DEFAULTS.retryOn.includes(this.status);
  }
}

export async function request(url, { method = "GET", body, ...options } = {}) {
  const config = { ...DEFAULTS, ...options };
  let lastError = null;

  for (let attempt = 0; attempt <= config.retries; attempt++) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), config.timeoutMs);

    try {
      const response = await fetch(url, {
        method,
        signal: controller.signal,
        headers: { "content-type": "application/json" },
        body: body ? JSON.stringify(body) : undefined,
      });

      if (!response.ok) throw new HttpError(response, await response.text());
      return await response.json();
    } catch (error) {
      lastError = error;
      if (error.name === "AbortError") console.warn(`timeout on ${url}`);
      if (error instanceof HttpError && !error.retryable) throw error;
      await sleep(config.backoffMs * 2 ** attempt);
    } finally {
      clearTimeout(timer);
    }
  }

  throw lastError ?? new Error(`gave up after ${DEFAULTS.retries} retries`);
}
