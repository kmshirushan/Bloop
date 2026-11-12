import { BloopScene } from './scene/BloopScene';

const container = document.getElementById('app');
if (!container) {
  throw new Error('Missing #app container element');
}

const bloopScene = new BloopScene(container);
bloopScene.start();

window.addEventListener('beforeunload', () => {
  bloopScene.dispose();
});

