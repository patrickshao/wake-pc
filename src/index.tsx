import {
  ButtonItem,
  Field,
  PanelSection,
  PanelSectionRow,
  TextField,
  ToggleField,
  staticClasses,
} from "@decky/ui";
import { callable, definePlugin, toaster } from "@decky/api";
import { type ReactNode, useEffect, useMemo, useRef, useState } from "react";
import { delay, errorMessage, retryWithTimeout, withTimeout } from "./runtime";

type HostStatus =
  | "Unknown"
  | "Checking"
  | "Online"
  | "Offline"
  | "Waking"
  | "Sleeping";

interface Config {
  mac_address: string;
  host_ip: string;
  broadcast_address: string;
  wake_port: number;
  status_port: number;
  status_enabled: boolean;
  sleep_enabled: boolean;
  sleep_port: number;
  sleep_paired: boolean;
}

interface DraftConfig {
  mac_address: string;
  host_ip: string;
  broadcast_address: string;
  wake_port: string;
  status_port: string;
  status_enabled: boolean;
  sleep_enabled: boolean;
  sleep_port: string;
}

type SaveConfigPayload = Omit<Config, "sleep_paired">;

interface SaveResult {
  ok: boolean;
  config?: Config;
  message: string;
}

interface ActionResult {
  ok: boolean;
  message: string;
}

interface StatusResult extends ActionResult {
  enabled: boolean;
  online: boolean;
  version?: string;
}

const DEFAULT_CONFIG: Config = {
  mac_address: "",
  host_ip: "",
  broadcast_address: "",
  wake_port: 9,
  status_port: 27036,
  status_enabled: true,
  sleep_enabled: false,
  sleep_port: 47653,
  sleep_paired: false,
};

const getConfig = callable<[], Config>("get_config");
const saveConfig = callable<[config: SaveConfigPayload], SaveResult>("save_config");
const sendWake = callable<[], ActionResult>("send_wake");
const checkStatus = callable<[], StatusResult>("check_status");
const sendSleep = callable<[], ActionResult>("send_sleep");
const checkSleepBridge = callable<[], StatusResult>("check_sleep_bridge");
const pairSleepBridge = callable<
  [config: SaveConfigPayload, pairingCode: string],
  SaveResult
>("pair_sleep_bridge");

const INITIAL_LOAD_TIMEOUT_MS = 2500;
const INITIAL_LOAD_ATTEMPTS = 3;
const BACKEND_ACTION_TIMEOUT_MS = 5000;
const STATUS_CHECK_TIMEOUT_MS = 4000;
const RELEASE_UPDATE_URL = "https://github.com/patrickshao/wake-pc/releases/latest/download/wake-pc-latest.zip";
const PLUGIN_VERSION = "1.0.0";

const STATUS_COLORS: Record<HostStatus, string> = {
  Unknown: "#8b929a",
  Checking: "#e7b85c",
  Online: "#66c0f4",
  Offline: "#d86f6f",
  Waking: "#f0c96b",
  Sleeping: "#f0c96b",
};

const toDraft = (config: Config): DraftConfig => ({
  mac_address: config.mac_address,
  host_ip: config.host_ip,
  broadcast_address: config.broadcast_address,
  wake_port: String(config.wake_port),
  status_port: String(config.status_port),
  status_enabled: config.status_enabled,
  sleep_enabled: config.sleep_enabled,
  sleep_port: String(config.sleep_port),
});

const toSavePayload = (draft: DraftConfig): SaveConfigPayload => ({
  mac_address: draft.mac_address,
  host_ip: draft.host_ip,
  broadcast_address: draft.broadcast_address,
  wake_port: Number(draft.wake_port),
  status_port: Number(draft.status_port),
  status_enabled: draft.status_enabled,
  sleep_enabled: draft.sleep_enabled,
  sleep_port: Number(draft.sleep_port),
});

const loadConfigWithRetry = (): Promise<Config> =>
  retryWithTimeout(
    getConfig,
    INITIAL_LOAD_ATTEMPTS,
    INITIAL_LOAD_TIMEOUT_MS,
    500,
    "Settings backend did not respond",
  );

const copyText = async (value: string): Promise<void> => {
  const textArea = document.createElement("textarea");
  textArea.value = value;
  textArea.readOnly = true;
  textArea.style.position = "fixed";
  textArea.style.opacity = "0";
  document.body.appendChild(textArea);
  try {
    textArea.focus();
    textArea.select();
    textArea.setSelectionRange(0, value.length);
    if (document.execCommand("copy")) return;
  } finally {
    textArea.remove();
  }

  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(value);
    return;
  }
  throw new Error("Steam clipboard unavailable");
};

function LineIcon({ children }: { children: ReactNode }) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      width="1em"
      height="1em"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      {children}
    </svg>
  );
}

const ComputerIcon = () => (
  <LineIcon>
    <rect x="3" y="4" width="18" height="13" rx="1" />
    <path d="M8 21h8M12 17v4" />
  </LineIcon>
);

function StatusBadge({ status }: { status: HostStatus }) {
  return (
    <div
      style={{
        display: "inline-flex",
        alignItems: "center",
        gap: "8px",
        fontWeight: 600,
      }}
    >
      <span
        aria-hidden="true"
        style={{
          width: "10px",
          height: "10px",
          borderRadius: "50%",
          background: STATUS_COLORS[status],
          boxShadow:
            status === "Waking" || status === "Sleeping" || status === "Checking"
              ? `0 0 0 4px ${STATUS_COLORS[status]}30`
              : "none",
        }}
      />
      {status}
    </div>
  );
}

function Content() {
  const [config, setConfig] = useState<Config>(DEFAULT_CONFIG);
  const [draft, setDraft] = useState<DraftConfig>(toDraft(DEFAULT_CONFIG));
  const [status, setStatus] = useState<HostStatus>("Unknown");
  const [statusMessage, setStatusMessage] = useState("Loading saved settings…");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [waking, setWaking] = useState(false);
  const [sleeping, setSleeping] = useState(false);
  const [pairing, setPairing] = useState(false);
  const [pairingCode, setPairingCode] = useState("");
  const [showPairing, setShowPairing] = useState(false);
  const [sleepArmed, setSleepArmed] = useState(false);
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [showPcDetails, setShowPcDetails] = useState(false);
  const [bridgeVersionLabel, setBridgeVersionLabel] = useState("Not paired");
  const [versionRefresh, setVersionRefresh] = useState(0);
  const pollGeneration = useRef(0);
  const draftEdited = useRef(false);
  const sleepArmTimer = useRef<number | null>(null);

  const dirty = useMemo(
    () => JSON.stringify(draft) !== JSON.stringify(toDraft(config)),
    [config, draft],
  );
  const needsSetup = !config.mac_address || (config.status_enabled && !config.host_ip);
  const actionsDisabled = loading || saving || waking || sleeping || pairing || dirty;
  const visibleStatusMessage = !loading && dirty
    ? "Save changes to use these settings."
    : statusMessage;

  useEffect(() => {
    let mounted = true;
    void loadConfigWithRetry()
      .then((loaded) => {
        if (!mounted) return;
        const complete = { ...DEFAULT_CONFIG, ...loaded };
        setConfig(complete);
        if (!draftEdited.current) setDraft(toDraft(complete));
        if (!complete.mac_address || (complete.status_enabled && !complete.host_ip)) {
          setStatusMessage("Add your PC in Settings below.");
        } else if (!complete.status_enabled) {
          setStatusMessage("Status checking is off.");
        } else {
          void refresh();
        }
      })
      .catch((error: unknown) => {
        if (!mounted) return;
        const message = errorMessage(error);
        setStatus("Unknown");
        setStatusMessage(`Could not load settings: ${message}`);
        toaster.toast({ title: "Could not load settings", body: message });
      })
      .finally(() => {
        if (mounted) setLoading(false);
      });

    return () => {
      mounted = false;
      pollGeneration.current += 1;
      if (sleepArmTimer.current !== null) {
        window.clearTimeout(sleepArmTimer.current);
      }
    };
  }, []);

  useEffect(() => {
    let mounted = true;
    if (!config.sleep_paired) {
      setBridgeVersionLabel("Not paired");
      return () => {
        mounted = false;
      };
    }

    setBridgeVersionLabel("Checking…");
    void withTimeout(
      checkSleepBridge(),
      STATUS_CHECK_TIMEOUT_MS,
      "Sleep Bridge check did not respond",
    )
      .then((result) => {
        if (!mounted) return;
        if (!result.ok) {
          setBridgeVersionLabel("Unavailable");
        } else if (!result.online) {
          setBridgeVersionLabel("Not reachable — check saved IP");
        } else if (result.version) {
          setBridgeVersionLabel(`Version ${result.version}`);
        } else {
          setBridgeVersionLabel("Older companion — version unavailable");
        }
      })
      .catch(() => {
        if (mounted) setBridgeVersionLabel("Unavailable");
      });

    return () => {
      mounted = false;
    };
  }, [config.host_ip, config.sleep_paired, config.sleep_port, versionRefresh]);

  const setField = <Key extends keyof DraftConfig>(
    key: Key,
    value: DraftConfig[Key],
  ) => {
    draftEdited.current = true;
    setDraft((current) => ({ ...current, [key]: value }));
  };

  const clearSleepConfirmation = () => {
    if (sleepArmTimer.current !== null) {
      window.clearTimeout(sleepArmTimer.current);
      sleepArmTimer.current = null;
    }
    setSleepArmed(false);
  };

  const refresh = async (): Promise<boolean> => {
    clearSleepConfirmation();
    setVersionRefresh((current) => current + 1);
    const generation = ++pollGeneration.current;
    setStatus("Checking");
    setStatusMessage("Checking…");
    try {
      const result = await withTimeout(
        checkStatus(),
        STATUS_CHECK_TIMEOUT_MS,
        "Status backend did not respond",
      );
      if (generation !== pollGeneration.current) return false;
      if (!result.enabled) {
        setStatus("Unknown");
        setStatusMessage("Status checking is off.");
        return false;
      }
      if (!result.ok) {
        setStatus("Unknown");
        setStatusMessage(result.message);
        return false;
      }
      setStatus(result.online ? "Online" : "Offline");
      setStatusMessage(
        result.online
          ? "PC is reachable."
          : "PC isn't reachable. Check the saved address or open Setup & status on Windows.",
      );
      return result.online;
    } catch (error: unknown) {
      if (generation !== pollGeneration.current) return false;
      const message = errorMessage(error);
      setStatus("Unknown");
      setStatusMessage(message);
      return false;
    }
  };

  const wake = async () => {
    clearSleepConfirmation();
    const generation = ++pollGeneration.current;
    setWaking(true);
    setStatus("Waking");
    setStatusMessage("Sending wake signal…");
    try {
      let wakeResult: ActionResult;
      try {
        wakeResult = await withTimeout(
          sendWake(),
          BACKEND_ACTION_TIMEOUT_MS,
          "Wake backend did not respond",
        );
      } catch (error: unknown) {
        if (generation !== pollGeneration.current) return;
        const message = errorMessage(error);
        setStatus("Unknown");
        setStatusMessage(message);
        toaster.toast({ title: "Wake failed", body: message });
        return;
      }
      if (generation !== pollGeneration.current) return;
      if (!wakeResult.ok) {
        setStatus("Unknown");
        setStatusMessage(wakeResult.message);
        toaster.toast({ title: "Wake failed", body: wakeResult.message });
        return;
      }
      if (!config.status_enabled) {
        setStatus("Unknown");
        setStatusMessage("Wake sent. Status checking is off.");
        return;
      }

      setStatusMessage("Wake sent. Waiting for the PC…");
      for (let attempt = 0; attempt < 30; attempt += 1) {
        await delay(2000);
        if (generation !== pollGeneration.current) return;
        let result: StatusResult;
        try {
          result = await withTimeout(
            checkStatus(),
            STATUS_CHECK_TIMEOUT_MS,
            "Status backend did not respond",
          );
        } catch (error: unknown) {
          if (generation !== pollGeneration.current) return;
          const message = errorMessage(error);
          setStatus("Unknown");
          setStatusMessage(`Wake packet sent. Status check unavailable: ${message}`);
          return;
        }
        if (generation !== pollGeneration.current) return;
        if (!result.enabled) {
          setStatus("Unknown");
          setStatusMessage("Wake sent. Status checking is off.");
          return;
        }
        if (!result.ok) {
          setStatus("Unknown");
          setStatusMessage(`Wake packet sent. Status check unavailable: ${result.message}`);
          return;
        }
        if (result.ok && result.enabled && result.online) {
          setStatus("Online");
          setStatusMessage("PC is reachable.");
          return;
        }
      }
      if (generation === pollGeneration.current) {
        setStatus("Offline");
        setStatusMessage("Wake sent. PC not responding yet.");
      }
    } finally {
      setWaking(false);
    }
  };

  const sleep = async () => {
    if (!sleepArmed) {
      setSleepArmed(true);
      setStatusMessage("Confirm sleep within 8 seconds.");
      if (sleepArmTimer.current !== null) {
        window.clearTimeout(sleepArmTimer.current);
      }
      sleepArmTimer.current = window.setTimeout(() => {
        sleepArmTimer.current = null;
        setSleepArmed(false);
        setStatusMessage("Sleep cancelled.");
      }, 8000);
      return;
    }

    clearSleepConfirmation();
    const generation = ++pollGeneration.current;
    setSleeping(true);
    setStatus("Sleeping");
    setStatusMessage("Sending sleep request…");
    let accepted = false;
    try {
      const result = await withTimeout(
        sendSleep(),
        BACKEND_ACTION_TIMEOUT_MS,
        "Sleep backend did not respond",
      );
      if (generation !== pollGeneration.current) return;
      if (!result.ok) {
        setStatus("Unknown");
        setStatusMessage(result.message);
        toaster.toast({ title: "Sleep failed", body: result.message });
        return;
      }

      accepted = true;
      setStatusMessage("Sleep accepted. Checking reachability…");
      for (let attempt = 0; attempt < 20; attempt += 1) {
        await delay(1000);
        if (generation !== pollGeneration.current) return;
        const bridge = await withTimeout(
          checkSleepBridge(),
          STATUS_CHECK_TIMEOUT_MS,
          "Sleep Bridge check did not respond",
        );
        if (generation !== pollGeneration.current) return;
        if (!bridge.ok) {
          setStatus("Unknown");
          setStatusMessage(bridge.message);
          return;
        }
        if (bridge.enabled && !bridge.online) {
          setStatus("Offline");
          setStatusMessage("Sleep accepted. Companion is no longer reachable.");
          toaster.toast({ title: "Sleep requested", body: "Companion is no longer reachable." });
          return;
        }
      }

      if (generation === pollGeneration.current) {
        setStatus("Unknown");
        setStatusMessage("Sleep was accepted, but the PC is still reachable.");
        toaster.toast({
          title: "Sleep not confirmed",
          body: "The PC is still reachable.",
        });
      }
    } catch (error: unknown) {
      if (generation !== pollGeneration.current) return;
      const message = errorMessage(error);
      setStatus("Unknown");
      setStatusMessage(accepted ? `Sleep accepted. Status unavailable: ${message}` : message);
      toaster.toast({ title: "Sleep not confirmed", body: message });
    } finally {
      setSleeping(false);
    }
  };

  const save = async () => {
    setSaving(true);
    try {
      const result = await withTimeout(
        saveConfig(toSavePayload(draft)),
        BACKEND_ACTION_TIMEOUT_MS,
        "Settings backend did not respond",
      );
      toaster.toast({
        title: result.ok ? "Settings saved" : "Settings not saved",
        body: result.message,
      });
      if (result.ok && result.config) {
        pollGeneration.current += 1;
        draftEdited.current = false;
        setConfig(result.config);
        setDraft(toDraft(result.config));
        setStatus("Unknown");
        setStatusMessage("Settings saved.");
        setShowPcDetails(false);
        setShowAdvanced(false);
      }
    } catch (error: unknown) {
      toaster.toast({ title: "Settings not saved", body: errorMessage(error) });
    } finally {
      setSaving(false);
    }
  };

  const pair = async () => {
    setPairing(true);
    try {
      const result = await withTimeout(
        pairSleepBridge(toSavePayload(draft), pairingCode),
        BACKEND_ACTION_TIMEOUT_MS,
        "Pairing backend did not respond",
      );
      toaster.toast({
        title: result.ok ? "Sleep control paired" : "Pairing failed",
        body: result.message,
      });
      if (!result.ok) {
        setStatusMessage(`Pairing failed: ${result.message}`);
      }
      if (result.ok && result.config) {
        draftEdited.current = false;
        setConfig(result.config);
        setDraft(toDraft(result.config));
        setPairingCode("");
        setShowPairing(false);
        setStatusMessage("Sleep control paired.");
      }
    } catch (error: unknown) {
      const message = errorMessage(error);
      setStatusMessage(`Pairing failed: ${message}`);
      toaster.toast({ title: "Pairing failed", body: message });
    } finally {
      setPairing(false);
    }
  };

  const copyUpdateUrl = async () => {
    try {
      await copyText(RELEASE_UPDATE_URL);
      toaster.toast({
        title: "URL copied",
        body: "Paste it in Decky’s Install Plugin from URL.",
      });
    } catch (error: unknown) {
      const message = errorMessage(error);
      setStatusMessage(`Copy failed: ${message}`);
      toaster.toast({ title: "Could not copy URL", body: message });
    }
  };

  return (
    <>
      <PanelSection title="Gaming PC">
        <PanelSectionRow>
          <Field label="Status" bottomSeparator="none">
            <StatusBadge status={status} />
          </Field>
          <div aria-live="polite" style={{ margin: "2px 0 10px", opacity: 0.72, lineHeight: 1.35, height: "2.7em", overflowY: "auto", overflowWrap: "anywhere" }}>
            {visibleStatusMessage}
          </div>
        </PanelSectionRow>
        <PanelSectionRow>
          <ButtonItem
            layout="below"
            disabled={actionsDisabled || !config.host_ip || !config.status_enabled}
            onClick={() => void refresh()}
          >
            Refresh status
          </ButtonItem>
        </PanelSectionRow>
      </PanelSection>

      <PanelSection title="Power">
        <PanelSectionRow>
          <ButtonItem
            layout="below"
            disabled={actionsDisabled || needsSetup}
            onClick={() => void wake()}
          >
            {waking ? "Waking…" : "Wake PC"}
          </ButtonItem>
        </PanelSectionRow>
        {config.sleep_enabled && config.sleep_paired && (
          <PanelSectionRow>
            <ButtonItem
              layout="below"
              disabled={actionsDisabled || !config.host_ip}
              onClick={() => void sleep()}
            >
              {sleeping ? "Sleeping…" : sleepArmed ? "Confirm sleep" : "Sleep PC"}
            </ButtonItem>
          </PanelSectionRow>
        )}
      </PanelSection>

      <PanelSection title="Settings">
        {!loading && !needsSetup && (
          <PanelSectionRow>
            <ButtonItem
              layout="below"
              disabled={saving || waking || sleeping || pairing || dirty}
              onClick={() => setShowPcDetails((current) => !current)}
            >
              {showPcDetails ? "Hide PC details" : "Edit PC details"}
            </ButtonItem>
          </PanelSectionRow>
        )}
        {!loading && (needsSetup || showPcDetails) && <>
        <PanelSectionRow>
          <TextField
            label="MAC address"
            description="Wired Ethernet adapter"
            value={draft.mac_address}
            disabled={saving || waking || sleeping || pairing}
            onChange={(event) => setField("mac_address", event.target.value)}
          />
        </PanelSectionRow>
        <PanelSectionRow>
          <TextField
            label="IP address"
            description="Use a reserved address"
            value={draft.host_ip}
            disabled={saving || waking || sleeping || pairing}
            onChange={(event) => setField("host_ip", event.target.value)}
          />
        </PanelSectionRow>
        <PanelSectionRow>
          <ToggleField
            label="Status checking"
            description="Show when the PC is reachable"
            checked={draft.status_enabled}
            disabled={saving || waking || sleeping || pairing}
            onChange={(checked) => setField("status_enabled", checked)}
          />
        </PanelSectionRow>
        {config.sleep_paired && (
          <PanelSectionRow>
            <ToggleField
              label="Show Sleep PC"
              description="Add Sleep PC to the Power section"
              checked={draft.sleep_enabled}
              disabled={saving || waking || sleeping || pairing}
              onChange={(checked) => setField("sleep_enabled", checked)}
            />
          </PanelSectionRow>
        )}
        <PanelSectionRow>
          <ButtonItem
            layout="below"
            disabled={saving || waking || sleeping || pairing}
            onClick={() => setShowAdvanced((current) => !current)}
          >
            {showAdvanced ? "Hide advanced settings" : "Advanced settings"}
          </ButtonItem>
        </PanelSectionRow>
        {showAdvanced && (
          <>
            <PanelSectionRow>
              <TextField
                label="Broadcast address"
                description="Optional"
                value={draft.broadcast_address}
                disabled={saving || waking || sleeping || pairing}
                onChange={(event) => setField("broadcast_address", event.target.value)}
              />
            </PanelSectionRow>
            <PanelSectionRow>
              <TextField
                label="Wake port"
                mustBeNumeric
                value={draft.wake_port}
                disabled={saving || waking || sleeping || pairing}
                onChange={(event) => setField("wake_port", event.target.value)}
              />
            </PanelSectionRow>
            <PanelSectionRow>
              <TextField
                label="Status port"
                description="Steam Remote Play uses 27036"
                mustBeNumeric
                value={draft.status_port}
                disabled={
                  saving || waking || sleeping || pairing || !draft.status_enabled
                }
                onChange={(event) => setField("status_port", event.target.value)}
              />
            </PanelSectionRow>
            <PanelSectionRow>
              <TextField
                label="Sleep Bridge port"
                description="Default is 47653"
                mustBeNumeric
                value={draft.sleep_port}
                disabled={saving || waking || sleeping || pairing}
                onChange={(event) => setField("sleep_port", event.target.value)}
              />
            </PanelSectionRow>
          </>
        )}
        <PanelSectionRow>
          <ButtonItem
            layout="below"
            disabled={loading || saving || waking || sleeping || pairing || !dirty}
            onClick={() => void save()}
          >
            {saving ? "Saving…" : "Save changes"}
          </ButtonItem>
        </PanelSectionRow>
        </>}
      </PanelSection>

      <PanelSection title="Sleep PC">
        <PanelSectionRow>
          <Field label="This device" bottomSeparator="none">
            <span style={{ fontWeight: 600 }}>
              {config.sleep_paired ? "Enabled" : "Not enabled"}
            </span>
          </Field>
          <div aria-live="polite" style={{ margin: "2px 0 10px", opacity: 0.72, lineHeight: 1.35, height: "2.7em", overflowY: "auto", overflowWrap: "anywhere" }}>
            {config.sleep_paired
              ? "This device can ask the Windows companion to put the PC to sleep."
              : "In the Windows companion, choose Pair for Sleep and enter its six-digit code here."}
          </div>
        </PanelSectionRow>
        {(!config.sleep_paired || showPairing) && (
          <>
            <PanelSectionRow>
              <TextField
                label="Pairing code"
                description="Six digits from the Windows tray icon"
                mustBeNumeric
                value={pairingCode}
                disabled={saving || waking || sleeping || pairing}
                onChange={(event) =>
                  setPairingCode(event.target.value.replace(/\D/g, "").slice(0, 6))
                }
              />
            </PanelSectionRow>
            <PanelSectionRow>
              <ButtonItem
                layout="below"
                disabled={
                  loading ||
                  saving ||
                  waking ||
                  sleeping ||
                  pairing ||
                  pairingCode.length !== 6
                }
                onClick={() => void pair()}
              >
                {pairing
                  ? "Pairing…"
                  : config.sleep_paired
                    ? "Re-pair this device"
                    : "Pair for Sleep"}
              </ButtonItem>
            </PanelSectionRow>
            {config.sleep_paired && showPairing && (
              <PanelSectionRow>
                <ButtonItem
                  layout="below"
                  disabled={saving || waking || sleeping || pairing}
                  onClick={() => {
                    setPairingCode("");
                    setShowPairing(false);
                    setStatusMessage("Sleep control remains paired.");
                  }}
                >
                  Cancel re-pairing
                </ButtonItem>
              </PanelSectionRow>
            )}
          </>
        )}
        {config.sleep_paired && !showPairing && (
          <PanelSectionRow>
            <ButtonItem
              layout="below"
              disabled={saving || waking || sleeping || pairing}
              onClick={() => {
                setPairingCode("");
                setShowPairing(true);
              }}
            >
              Re-pair this device…
            </ButtonItem>
          </PanelSectionRow>
        )}
      </PanelSection>

      <PanelSection title="Update">
        <PanelSectionRow>
          <Field label="Decky plugin" bottomSeparator="none">
            <span style={{ fontWeight: 600 }}>Version {PLUGIN_VERSION}</span>
          </Field>
        </PanelSectionRow>
        <PanelSectionRow>
          <Field label="Windows companion" bottomSeparator="none">
            <span style={{ fontWeight: 600 }}>{bridgeVersionLabel}</span>
          </Field>
        </PanelSectionRow>
        <PanelSectionRow>
          <TextField
            label="Decky update URL"
            value={RELEASE_UPDATE_URL}
            onChange={() => undefined}
          />
        </PanelSectionRow>
        <PanelSectionRow>
          <ButtonItem
            layout="below"
            onClick={() => void copyUpdateUrl()}
          >
            Copy update URL
          </ButtonItem>
        </PanelSectionRow>
      </PanelSection>
    </>
  );
}

export default definePlugin(() => ({
  name: "Wake PC",
  titleView: <div className={staticClasses.Title}>Wake PC</div>,
  content: <Content />,
  icon: <ComputerIcon />,
}));
