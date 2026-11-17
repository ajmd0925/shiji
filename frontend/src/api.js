export async function api(path, options = {}) {
  const token = sessionStorage.getItem("assistant-token") || "";
  const response = await fetch(`/api${path}`, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
      ...options.headers,
    },
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    const error = new Error(body.message || `请求失败（${response.status}）`);
    error.status = response.status;
    throw error;
  }
  return response.status === 204 ? null : response.json();
}
export const send = (path, data, method = "POST") =>
  api(path, { method, body: JSON.stringify(data) });
