import { defineConfig } from "vitepress";

const docsRoot = "https://docs.boltz.exchange";

// https://vitepress.dev/reference/site-config
export default defineConfig({
  title: "Boltz BTCPay Server Plugin",
  description: "Boltz BTCPay Server Plugin Docs",
  themeConfig: {
    logo: "./assets/logo.svg",
    search: {
      provider: "local",
    },
    nav: [{ text: "Home", link: docsRoot }],
    sidebar: [
      {
        items: [
          { text: "👋 Introduction", link: "/index" },
          { text: "🚧 Limitations", link: "/limitations" },
          { text: "🏗️ Building the Plugin", link: "/building-the-plugin" },
          { text: "🧪 Regtest Setup", link: "/regtest-setup" },

          { text: "🔙 Home", link: docsRoot },
        ],
      },
    ],
    socialLinks: [
      {
        icon: "github",
        link: "https://github.com/BoltzExchange/boltz-btcpay-plugin",
      },
    ],
  },
  // Ignore dead links to localhost
  ignoreDeadLinks: [/https?:\/\/localhost/],
});
