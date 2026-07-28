// nib highlight fixture
const DEFAULTS = { retries: 3, timeout: 5_000 };

export async function fetchJson(url, options = {}) {
  const { retries } = { ...DEFAULTS, ...options };
  for (let attempt = 0; attempt < retries; attempt++) {
    const response = await fetch(url);
    if (response.ok) return response.json();
  }
  throw new Error(`giving up on ${url}`);
}
