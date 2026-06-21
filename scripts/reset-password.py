import os
import sys
import json
import base64
import hashlib
import getpass

def generate_password_hash(password: str) -> str:
    """Generates a PBKDF2-SHA256 hash matching the C# PasswordHasher implementation."""
    salt = os.urandom(16)
    # PBKDF2-SHA256, 600,000 iterations, 32 bytes key length
    key = hashlib.pbkdf2_hmac('sha256', password.encode('utf-8'), salt, 600000, dklen=32)
    salt_b64 = base64.b64encode(salt).decode('utf-8')
    key_b64 = base64.b64encode(key).decode('utf-8')
    return f"{salt_b64}:{key_b64}:600000"

def reset_password(config_path: str, new_password: str, username: str = "admin"):
    if not os.path.exists(config_path):
        print(f"Error: Configuration file not found at {config_path}")
        sys.exit(1)

    with open(config_path, 'r', encoding='utf-8') as f:
        config = json.load(f)

    if 'auth' not in config:
        config['auth'] = {}

    config['auth']['username'] = username
    config['auth']['passwordHash'] = generate_password_hash(new_password)

    with open(config_path, 'w', encoding='utf-8') as f:
        json.dump(config, f, indent=2)

    print(f"\nSuccess! Password for '{username}' has been reset.")
    print("Please restart the application in Visual Studio for changes to take effect.")

def clear_credentials(config_path: str):
    if not os.path.exists(config_path):
        print(f"Error: Configuration file not found at {config_path}")
        sys.exit(1)

    with open(config_path, 'r', encoding='utf-8') as f:
        config = json.load(f)

    if 'auth' in config:
        config['auth']['username'] = ""
        config['auth']['passwordHash'] = ""

    with open(config_path, 'w', encoding='utf-8') as f:
        json.dump(config, f, indent=2)

    print("\nSuccess! Credentials have been cleared.")
    print("The application will prompt you to set up a new admin account on the next run.")

if __name__ == "__main__":
    # Default path relative to the repository root (one level up from the scripts directory)
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    default_config_path = os.path.join(repo_root, "src", "MqttProbe.Web", "config", "appsettings.json")
    
    # Allow overriding via command line argument
    config_path = sys.argv[1] if len(sys.argv) > 1 else default_config_path
    
    print(f"Target config file: {config_path}\n")
    print("1. Set a new password")
    print("2. Clear credentials (forces Setup screen on next run, preserves connections)")
    
    choice = input("Choose an option (1 or 2): ").strip()
    
    if choice == "2":
        clear_credentials(config_path)
    else:
        username = input("Enter new username (default: admin): ").strip() or "admin"
        
        try:
            new_password = getpass.getpass("Enter new password: ")
            confirm_password = getpass.getpass("Confirm new password: ")
        except Exception:
            # Fallback for IDE consoles where getpass might not hide input properly
            print("(Note: Input may be visible in this console)")
            new_password = input("Enter new password: ").strip()
            confirm_password = input("Confirm new password: ").strip()
        
        if new_password != confirm_password:
            print("Error: Passwords do not match.")
            sys.exit(1)
            
        if not new_password:
            print("Error: Password cannot be empty.")
            sys.exit(1)

        reset_password(config_path, new_password, username)
