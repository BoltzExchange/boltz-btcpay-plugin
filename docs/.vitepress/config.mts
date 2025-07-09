import { defineConfig } from "vitepress";

const docsRoot = "https://docs.boltz.exchange";

// https://vitepress.dev/reference/site-config
export default defineConfig({
  title: "Boltz BTCPay Plugin",
  description: "Boltz BTCPay Plugin Docs",
  head: [
    ['link', { rel: 'icon', href: '/assets/logo.svg' }],
  ],
  themeConfig: {
    logo: "/assets/logo.svg",
    search: {
      provider: "local",
    },
    nav: [{ text: "🏠 Docs Home", link: docsRoot, target: '_self' }],
    sidebar: [
      {
        items: [
          { text: "👋 Introduction", link: "/index" },
          { text: "🚧 Limitations", link: "/limitations" },
          { text: "🏗️ Building the Plugin", link: "/building-the-plugin" },
          { text: "🧪 Regtest Setup", link: "/regtest-setup" },
          { text: "🏠 Docs Home", link: docsRoot, target: '_self' },
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
