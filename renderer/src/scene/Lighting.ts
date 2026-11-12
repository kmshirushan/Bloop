import * as THREE from 'three';

/**
 * Configures three-point studio lighting and a procedural PMREM environment map
 * so the electric-blue mascot sphere has crisp glossy clearcoat reflections
 * without external HDR texture dependencies.
 */
export class Lighting {
  public readonly group = new THREE.Group();
  private envMapTexture: THREE.Texture | null = null;

  constructor(scene: THREE.Scene, renderer: THREE.WebGLRenderer) {
    // 1. Soft hemisphere ambient fill (sky blue top, deep indigo bottom)
    const hemiLight = new THREE.HemisphereLight(0x93c5fd, 0x0f172a, 1.35);
    hemiLight.position.set(0, 4, 0);
    this.group.add(hemiLight);

    // 2. Main key light (top-left front) for primary specular highlight
    const keyLight = new THREE.DirectionalLight(0xffffff, 2.5);
    keyLight.position.set(-2.4, 3.2, 4.0);
    this.group.add(keyLight);

    // 3. Cool cyan rim light (back-right) for spherical edge definition
    const rimLight = new THREE.DirectionalLight(0x38bdf8, 2.1);
    rimLight.position.set(3.0, 2.0, -3.0);
    this.group.add(rimLight);

    // 4. Subtle bottom bounce light for glossy underside lift
    const bounceLight = new THREE.PointLight(0x60a5fa, 1.1, 8.0);
    bounceLight.position.set(0.0, -2.5, 2.2);
    this.group.add(bounceLight);

    scene.add(this.group);

    // Generate a lightweight procedural studio environment for clearcoat reflections
    this.envMapTexture = this.createProceduralEnvironment(renderer);
    scene.environment = this.envMapTexture;
  }

  private createProceduralEnvironment(renderer: THREE.WebGLRenderer): THREE.Texture {
    const pmremGenerator = new THREE.PMREMGenerator(renderer);
    pmremGenerator.compileEquirectangularShader();

    const envScene = new THREE.Scene();
    envScene.background = new THREE.Color(0x0b192c);

    // Bright softbox panels reflected in the clearcoat
    const panelGeo = new THREE.PlaneGeometry(4, 4);
    const topPanelMat = new THREE.MeshBasicMaterial({ color: 0xffffff, side: THREE.DoubleSide });
    const topPanel = new THREE.Mesh(panelGeo, topPanelMat);
    topPanel.position.set(-3, 4, 4);
    topPanel.lookAt(0, 0, 0);
    envScene.add(topPanel);

    const rimPanelMat = new THREE.MeshBasicMaterial({ color: 0x38bdf8, side: THREE.DoubleSide });
    const rimPanel = new THREE.Mesh(panelGeo, rimPanelMat);
    rimPanel.position.set(4, 1.5, -3);
    rimPanel.lookAt(0, 0, 0);
    envScene.add(rimPanel);

    const renderTarget = pmremGenerator.fromScene(envScene, 0.04);
    pmremGenerator.dispose();
    panelGeo.dispose();
    topPanelMat.dispose();
    rimPanelMat.dispose();

    return renderTarget.texture;
  }

  public dispose(): void {
    this.envMapTexture?.dispose();
  }
}

