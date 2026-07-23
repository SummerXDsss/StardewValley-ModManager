import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Toast,
  ToastBody,
  ToastTitle,
  Toaster,
  useId,
  useToastController,
} from "@fluentui/react-components";
import type { ReactNode } from "react";
import { createContext, useCallback, useContext, useMemo, useState } from "react";

export type NoticeIntent = "success" | "error" | "warning" | "info";

interface ConfirmOptions {
  title: ReactNode;
  content: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
  onConfirm: () => void | Promise<void>;
}

interface AppUiContextValue {
  notify: (intent: NoticeIntent, title: string, body?: string) => void;
  confirm: (options: ConfirmOptions) => void;
}

const AppUiContext = createContext<AppUiContextValue | null>(null);

export function AppUiProvider({ children }: { children: ReactNode }) {
  const toasterId = useId("app-toaster");
  const { dispatchToast } = useToastController(toasterId);
  const [confirmation, setConfirmation] = useState<ConfirmOptions>();
  const [confirming, setConfirming] = useState(false);

  const notify = useCallback<AppUiContextValue["notify"]>((intent, title, body) => {
    dispatchToast(
      <Toast>
        <ToastTitle>{title}</ToastTitle>
        {body && <ToastBody>{body}</ToastBody>}
      </Toast>,
      {
        intent,
        timeout: intent === "error" ? 8000 : 4500,
        pauseOnHover: true,
        pauseOnWindowBlur: true,
      },
    );
  }, [dispatchToast]);

  const confirm = useCallback((options: ConfirmOptions) => {
    setConfirmation(options);
  }, []);

  const runConfirmation = async () => {
    if (!confirmation || confirming) return;
    setConfirming(true);
    try {
      await confirmation.onConfirm();
      setConfirmation(undefined);
    } catch (error) {
      notify("error", "操作未完成", String(error));
    } finally {
      setConfirming(false);
    }
  };

  const value = useMemo(() => ({ notify, confirm }), [confirm, notify]);

  return (
    <AppUiContext.Provider value={value}>
      {children}
      <Toaster toasterId={toasterId} position="top-end" limit={4} />
      <Dialog open={Boolean(confirmation)} modalType="alert">
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{confirmation?.title}</DialogTitle>
            <DialogContent>{confirmation?.content}</DialogContent>
            <DialogActions>
              <Button
                appearance="secondary"
                disabled={confirming}
                onClick={() => setConfirmation(undefined)}
              >
                {confirmation?.cancelLabel ?? "取消"}
              </Button>
              <Button
                appearance="primary"
                className={confirmation?.destructive ? "danger-button" : undefined}
                disabled={confirming}
                onClick={() => void runConfirmation()}
              >
                {confirming ? "正在处理" : confirmation?.confirmLabel ?? "确认"}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </AppUiContext.Provider>
  );
}

export function useAppUi(): AppUiContextValue {
  const value = useContext(AppUiContext);
  if (!value) throw new Error("useAppUi must be used inside AppUiProvider");
  return value;
}
