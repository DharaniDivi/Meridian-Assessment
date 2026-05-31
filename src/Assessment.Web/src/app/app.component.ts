import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <div style="max-width: 1100px; margin: 0 auto; padding: 1.5rem;">
      <header style="margin-bottom: 1.5rem;">
        <h1 style="margin: 0;">SE Assessment Console</h1>
        <p class="muted" style="margin: 0.25rem 0 0;">.NET Core + Angular puzzle runner</p>
      </header>
      <router-outlet />
    </div>
  `
})
export class AppComponent {}
