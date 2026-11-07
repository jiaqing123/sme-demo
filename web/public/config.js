// Development default, committed so `npm run dev` works without a container.
// The container entrypoint overwrites this file at start-up from the DEMO_*
// environment variables (web/nginx/40-runtime-config.sh). Never put real
// secrets here: the demo credentials are public by design.
window.__SME_CONFIG__ = {
  demoMode: true,
  demoUsername: "demo",
  demoPassword: "Demo_Passw0rd!",
  demoOtp: "123456",
};
