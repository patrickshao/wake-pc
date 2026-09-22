import { createRoot } from "react-dom/client";
import plugin from "../src/index";
createRoot(document.getElementById("panel")!).render(<><header>{plugin.titleView}</header>{plugin.content}</>);
window.addEventListener("preview-toast", (event: Event) => {
  const toast = document.getElementById("toast")!;
  toast.textContent = (event as CustomEvent).detail;
});
