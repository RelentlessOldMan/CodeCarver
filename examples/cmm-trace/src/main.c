/* The firmware entry. The image needs driver_start(); never_used() is dead. */
int driver_start(void);

int main(void)
{
    return driver_start();
}
