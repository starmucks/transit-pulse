/**
 * GET JSON from a same-origin API path. Throws with a short message on non-OK
 * responses so panels can show loading / empty / error without silent failure.
 */
export async function fetchJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {
    headers: { Accept: 'application/json' },
    signal,
  })

  if (response.status === 404) {
    throw new ApiNotFoundError(path)
  }

  if (!response.ok) {
    throw new Error(`Request failed (${response.status})`)
  }

  return (await response.json()) as T
}

/** Raised when the API returns 404 for an unknown stop or route. */
export class ApiNotFoundError extends Error {
  constructor(path: string) {
    super(`Not found: ${path}`)
    this.name = 'ApiNotFoundError'
  }
}
