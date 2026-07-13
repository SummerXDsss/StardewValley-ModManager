import React from "react";
import ReactDOM from "react-dom/client";
import { App as AntApp, ConfigProvider } from "antd";
import zhCN from "antd/locale/zh_CN";
import App from "./App";
import "./styles.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ConfigProvider
      locale={zhCN}
      theme={{
        token: {
          colorPrimary: "#2f6f4e",
          colorInfo: "#2f6f4e",
          colorSuccess: "#3f7d50",
          colorWarning: "#b7791f",
          colorError: "#b54a45",
          colorText: "#26352d",
          colorTextSecondary: "#66736b",
          colorBgBase: "#f7f5ee",
          colorBorder: "#d9ddd6",
          borderRadius: 6,
          fontFamily: '"Noto Sans SC", "PingFang SC", "Hiragino Sans GB", "Segoe UI", "Microsoft YaHei", sans-serif',
        },
        components: {
          Button: { controlHeight: 38 },
          Table: { headerBg: "#f0f2ed", headerColor: "#4d5f54" },
          Menu: { itemBorderRadius: 5, itemHeight: 44 },
        },
      }}
    >
      <AntApp>
        <App />
      </AntApp>
    </ConfigProvider>
  </React.StrictMode>,
);
