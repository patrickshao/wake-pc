const scenario = new URLSearchParams(location.search).get("state") ?? "setup";
let online = scenario === "online";
let config = {
  mac_address: scenario === "setup" ? "" : "00:11:22:33:44:55",
  host_ip: scenario === "setup" ? "" : "192.0.2.10",
  broadcast_address: "", wake_port: 9, status_port: 27036,
  status_enabled: true, sleep_enabled: scenario === "online",
  sleep_port: 47653, sleep_paired: scenario === "online",
};
export const definePlugin = <T>(factory: () => T): T => factory();
export const toaster = { toast({ title, body }: { title: string; body: string }) {
  window.dispatchEvent(new CustomEvent("preview-toast", { detail: `${title}: ${body}` }));
}};
export const callable = <Args extends unknown[], Result>(name: string) => async (...args: Args): Promise<Result> => {
  await new Promise(resolve => setTimeout(resolve, 150));
  if (scenario === "error" && name !== "get_config") throw new Error("PC did not respond. Check its IP address.");
  switch (name) {
    case "get_config": return { ...config } as Result;
    case "save_config": config = { ...config, ...(args[0] as object) }; return { ok: true, config: { ...config }, message: "Saved." } as Result;
    case "pair_sleep_bridge": config = { ...config, ...(args[0] as object), sleep_paired: true, sleep_enabled: true }; return { ok: true, config: { ...config }, message: "Paired." } as Result;
    case "send_wake": online = true; return { ok: true, message: "Wake sent." } as Result;
    case "send_sleep": online = false; return { ok: true, message: "Sleep accepted." } as Result;
    case "check_status": return { ok: true, enabled: config.status_enabled, online, message: "" } as Result;
    case "check_sleep_bridge": return { ok: true, enabled: true, online, version: online ? "1.0.0" : undefined, message: "" } as Result;
    default: throw new Error(`Unknown preview call: ${name}`);
  }
};
